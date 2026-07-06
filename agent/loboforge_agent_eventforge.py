#!/usr/bin/env python3
"""
LoboForge GPU Agent — EventForge transport
==========================================
Claims image jobs from EventForge HTTP API, runs Comfy/native via loboforge_agent,
uploads outputs with PUT /v1/jobs/{id}/output, completes with POST /complete.

Env:
    EVENT_FORGE_URL        — default http://localhost:8090
    EVENT_FORGE_WORKER_KEY — worker bearer token
    FORGE_QUEUE_CAPABILITY — comma-list of capabilities to poll
    LOBO_BASE_URL, LOBO_SECRET — hub LoRA catalog sync + request-work prefetch (check-in is EventForge-only)
    LOBO_LORA_SYNC_INTERVAL — seconds between active-loras pulls (default 1800)
"""

from __future__ import annotations

import argparse
import asyncio
import contextlib
import json
import logging
import os
import time
import uuid
from pathlib import Path
from typing import Any

import aiohttp

import loboforge_agent as agent
from loboforge_agent_common import (
    CHECK_IN_INTERVAL,
    _lora_basenames_match,
    _normalize_lora_basename,
    _sync_lora_inventory,
    build_check_in_payload,
    extract_required_loras_from_assign,
    resolve_claim_ready_capabilities,
    resolve_lora_sync_mode,
    sync_hub_active_loras,
    worker_can_run_assign,
    worker_has_lora,
)

# Reuse SQS helpers (LoRA prefetch, capability resolution).
from loboforge_agent_sqs import (  # noqa: E402
    NoopSession,
    _extract_assign_payload,
    check_assign_job_loras_sqs,
    resolve_capabilities,
    resolve_http_base,
    sqs_lora_prefetch_loop,
)

log = logging.getLogger("gpu-agent-ef")

def _resolve_ef_url() -> str:
    v = (os.environ.get("EVENT_FORGE_URL") or "").strip()
    return (v or "http://localhost:8090").rstrip("/")

DEFAULT_EF = _resolve_ef_url()
DEFAULT_WORKER_KEY = os.environ.get("EVENT_FORGE_WORKER_KEY", "wrath-worker-key")
CLAIM_IDLE_SECS = float(os.environ.get("EVENT_FORGE_CLAIM_IDLE", "2"))
UPLOAD_RETRIES = int(os.environ.get("EVENT_FORGE_UPLOAD_RETRIES", "5"))


class EfGpuSession:
    """HTTP session for loboforge_agent.handle_job — EventForge uploads, no MQTT/SQS."""

    def __init__(
        self,
        args: argparse.Namespace,
        http: aiohttp.ClientSession,
        ef_base: str,
        worker_key: str,
        job_meta: dict[str, Any],
    ):
        self._args = args
        self._http = http
        self._ef = ef_base
        self._key = worker_key
        self._meta = job_meta
        self._uuid = args.node_uuid
        self._current_job: str | None = None
        self.uploaded_outputs: list[tuple[str, str]] = []
        self.failed_reason: str | None = None
        self.hostname = args.hostname

    def set_current_job(self, job_uuid: str | None) -> None:
        self._current_job = job_uuid

    async def send_json(self, data: dict, topic: str | None = None) -> bool:
        mtype = data.get("type", "")
        if mtype == "job_failed":
            self.failed_reason = str(data.get("reason") or "worker failure")
        return True

    async def wait_response(self, types: set[str], timeout: float) -> dict:
        if "file_ready" in types:
            return {"type": "file_ready"}
        if "file_ok" in types:
            return {"type": "file_ok"}
        raise asyncio.TimeoutError()

    async def send_bytes(self, data: bytes) -> None:
        raise NotImplementedError("EventForge agent uses HTTP upload")

    async def send(self, raw: str) -> None:
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            return
        if data.get("type") == "download_error":
            log.warning("LoRA download error: %s", data.get("reason"))


async def send_file_ef(
    session: EfGpuSession,
    job_uuid: str,
    filename: str,
    data: bytes,
    mime_type: str,
) -> bool:
    ext = Path(filename).suffix or ".bin"
    out_name = f"output{ext}" if not filename else filename
    url = f"{session._ef}/v1/jobs/{job_uuid}/output?file={out_name}"
    headers = {
        "Authorization": f"Bearer {session._key}",
        "Content-Type": mime_type,
    }
    for attempt in range(1, UPLOAD_RETRIES + 1):
        try:
            async with session._http.put(url, data=data, headers=headers) as resp:
                if resp.status == 503:
                    retry_after = int(resp.headers.get("Retry-After", "5"))
                    log.warning(
                        "EventForge upload saturated job=%s attempt=%d retry=%ds",
                        job_uuid[:8], attempt, retry_after,
                    )
                    await asyncio.sleep(retry_after)
                    continue
                if resp.status >= 400:
                    body = await resp.text()
                    raise RuntimeError(f"upload HTTP {resp.status}: {body[:200]}")
                session.uploaded_outputs.append((out_name, mime_type))
                log.info("Uploaded output job=%s file=%s (%d bytes)", job_uuid[:8], out_name, len(data))
                return True
        except aiohttp.ClientError as ex:
            if attempt >= UPLOAD_RETRIES:
                raise
            log.warning("Upload attempt %d failed: %s", attempt, ex)
            await asyncio.sleep(min(2 ** attempt, 30))
    return False


async def accept_assign_job_ef(
    session: EfGpuSession,
    msg: dict,
    args: argparse.Namespace,
    state: dict,
) -> None:
    job_id = msg.get("job_uuid")
    session.set_current_job(job_id)
    state["current_job"] = job_id
    state["current_job_since"] = time.time()
    state["job_started"] = False
    original_send_file = agent.send_file

    async def _send_file_wrapped(ws, job_uuid, filename, data, mime_type):
        return await send_file_ef(session, job_uuid, filename, data, mime_type)

    agent.send_file = _send_file_wrapped  # type: ignore[assignment]
    try:
        await agent.handle_job(session, msg, args, state)
    except Exception as ex:
        log.exception("Job %s crashed in handle_job", job_id)
        session.failed_reason = str(ex)
    finally:
        agent.send_file = original_send_file
        session.set_current_job(None)
        state["current_job"] = None


async def ef_claim_any(
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
    capabilities: list[str],
    hostname: str,
) -> dict[str, Any] | None:
    url = f"{ef_base}/v1/jobs/claim"
    headers = {
        "Authorization": f"Bearer {worker_key}",
        "Content-Type": "application/json",
        "Accept": "application/json",
    }
    body = {"capabilities": capabilities, "hostname": hostname}
    async with http.post(url, json=body, headers=headers) as resp:
        if resp.status == 204:
            return None
        if resp.status == 401:
            raise RuntimeError("EventForge claim unauthorized — check EVENT_FORGE_WORKER_KEY")
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"EventForge claim HTTP {resp.status}: {text[:200]}")
        return await resp.json()



async def ef_release(
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
    job_id: str,
) -> bool:
    url = f"{ef_base}/v1/jobs/{job_id}/release"
    headers = {
        "Authorization": f"Bearer {worker_key}",
        "Content-Type": "application/json",
    }
    async with http.post(url, headers=headers) as resp:
        if resp.status >= 400:
            text_body = await resp.text()
            log.warning("EventForge release HTTP %s: %s", resp.status, text_body[:200])
            return False
        return True


async def ef_complete(
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
    job_id: str,
) -> None:
    url = f"{ef_base}/v1/jobs/{job_id}/complete"
    headers = {
        "Authorization": f"Bearer {worker_key}",
        "Content-Type": "application/json",
    }
    async with http.post(url, json={}, headers=headers) as resp:
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"EventForge complete HTTP {resp.status}: {text[:200]}")


async def ef_fail(
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
    job_id: str,
    error: str,
) -> None:
    url = f"{ef_base}/v1/jobs/{job_id}/fail"
    headers = {
        "Authorization": f"Bearer {worker_key}",
        "Content-Type": "application/json",
    }
    async with http.post(url, json={"error": error}, headers=headers) as resp:
        if resp.status >= 400:
            text = await resp.text()
            log.warning("EventForge fail HTTP %s: %s", resp.status, text[:200])


def _build_assign_from_claim(job: dict[str, Any]) -> dict | None:
    payload = job.get("payload")
    if isinstance(payload, dict):
        assign = _extract_assign_payload(payload)
        if assign:
            assign.setdefault("job_uuid", job.get("job_id"))
            return assign
        if payload.get("type") == "assign_job":
            out = dict(payload)
            out.setdefault("job_uuid", job.get("job_id"))
            return out
        inner = payload.get("payload")
        if isinstance(inner, dict):
            assign = _extract_assign_payload(inner)
            if assign:
                assign.setdefault("job_uuid", job.get("job_id"))
                return assign
    return None


async def process_ef_job(
    job: dict[str, Any],
    args: argparse.Namespace,
    state: dict,
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
) -> None:
    job_id = str(job.get("job_id") or "")
    capability = str(job.get("capability") or "")
    tier = str(job.get("tier") or "")
    log.info(
        "EventForge claimed %s capability=%s tier=%s app=%s",
        job_id[:8], capability, tier, job.get("app_id", "-"),
    )

    assign = _build_assign_from_claim(job)
    if not isinstance(assign, dict) or assign.get("type") != "assign_job":
        await ef_fail(http, ef_base, worker_key, job_id, "payload missing runnable assign_job")
        return

    assign.setdefault("job_uuid", job_id)

    if not worker_can_run_assign(state, assign, hostname=args.hostname, capability=capability):
        model_name = (assign.get("model") or "?").strip()
        msg = (
            f"server assigned job {job_id[:8]} with model {model_name} "
            f"but worker check-in did not report readiness — claim gate bug"
        )
        log.error(msg)
        await ef_fail(http, ef_base, worker_key, job_id, msg)
        return

    loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
    if not loras_ok:
        try:
            sync_result = await asyncio.to_thread(sync_hub_active_loras, args)
            log.info(
                "Pre-job LoRA sync mode=%s pulled=%s for missing: %s",
                sync_result.get("mode"),
                sync_result.get("pulled"),
                ", ".join(_normalize_lora_basename(l) for l in missing_loras),
            )
            await _sync_lora_inventory(state, args)
            loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
        except Exception as ex:
            log.warning("Pre-job LoRA sync failed: %s", ex)

    if not loras_ok:
        missing_names = ", ".join(_normalize_lora_basename(l) for l in missing_loras)
        defer_counts: dict[str, int] = state.setdefault("_ef_lora_defer_counts", {})
        count = defer_counts.get(job_id, 0) + 1
        defer_counts[job_id] = count
        max_defers = int(os.environ.get("FORGE_QUEUE_MAX_LORA_DEFERS", "15"))
        if count >= max_defers:
            await ef_fail(http, ef_base, worker_key, job_id, f"missing LoRAs after {count} attempts: {missing_names}")
            return
        log.info("Releasing job %s — missing LoRAs: %s (attempt %d)", job_id[:8], missing_names, count)
        await ef_release(http, ef_base, worker_key, job_id)
        from loboforge_agent_sqs import _enqueue_lora_prefetch
        _enqueue_lora_prefetch(state, missing_loras)
        return

    state.get("_ef_lora_defer_counts", {}).pop(job_id, None)
    session = EfGpuSession(args, http, ef_base, worker_key, job)
    session.uploaded_outputs.clear()
    session.failed_reason = None

    try:
        await accept_assign_job_ef(session, assign, args, state)
    except Exception as ex:
        log.exception("process_ef_job crashed for %s", job_id[:8])
        await ef_fail(http, ef_base, worker_key, job_id, str(ex))
        return

    if session.failed_reason:
        reason = session.failed_reason
        if reason.startswith("Text encoder not on worker"):
            log.info("Releasing job %s — %s", job_id[:8], reason)
            await ef_release(http, ef_base, worker_key, job_id)
        else:
            await ef_fail(http, ef_base, worker_key, job_id, reason)
    elif session.uploaded_outputs:
        await ef_complete(http, ef_base, worker_key, job_id)
        log.info("EventForge done %s → %s", job_id[:8], session.uploaded_outputs[-1][0])
    else:
        await ef_fail(http, ef_base, worker_key, job_id, "Worker produced no output")


async def ef_api_check_in_once(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    agent_state: dict[str, Any],
    ef_base: str | None = None,
    worker_key: str | None = None,
) -> bool:
    base = (ef_base or _resolve_ef_url()).rstrip("/")
    key = worker_key or os.environ.get("EVENT_FORGE_WORKER_KEY", DEFAULT_WORKER_KEY)
    url = f"{base}/v1/workers/check-in"
    headers = {
        "Authorization": f"Bearer {key}",
        "Content-Type": "application/json",
    }
    try:
        models = await agent.get_available_models(args.comfyui_http)
        agent_state["models"] = models
        known = agent_state.setdefault("known_loras", [])
        for name in models.get("loras") or []:
            base_name = _normalize_lora_basename(name)
            if base_name and not any(_lora_basenames_match(k, base_name) for k in known):
                known.append(base_name)
    except Exception as ex:
        log.warning("Model inventory refresh before check-in failed: %s", ex)

    caps = agent_state.get("forge_queue_capabilities") or []
    claim_ready = resolve_claim_ready_capabilities(agent_state, caps, hostname=args.hostname)
    agent_state["claim_ready_capabilities"] = claim_ready

    payload = build_check_in_payload(args, agent_state)
    payload["transport"] = "eventforge"
    try:
        async with http.post(url, json=payload, headers=headers) as resp:
            if resp.status != 200:
                body = await resp.text()
                log.warning("EventForge check-in failed HTTP %s: %s", resp.status, body[:200])
                agent_state["check_in_ok"] = False
                return False
            data = await resp.json()
            ack = data.get("acknowledged_models") or []
            agent_state["check_in_ok"] = True
            log.info(
                "EventForge check-in OK — claim_ready=%s (%d loras ack)",
                ",".join(claim_ready) or "none",
                len(ack),
            )
            return True
    except Exception as ex:
        log.warning("EventForge check-in error: %s", ex)
        agent_state["check_in_ok"] = False
        return False


async def ef_api_check_in_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    agent_state: dict[str, Any],
    ef_base: str | None = None,
    worker_key: str | None = None,
) -> None:
    await ef_api_check_in_once(http, args, agent_state, ef_base, worker_key)
    while True:
        await asyncio.sleep(CHECK_IN_INTERVAL)
        await ef_api_check_in_once(http, args, agent_state, ef_base, worker_key)


async def ef_consumer_loop(
    capabilities: tuple[str, ...],
    args: argparse.Namespace,
    state: dict,
    http: aiohttp.ClientSession,
    ef_base: str,
    worker_key: str,
) -> None:
    log.info(
        "EventForge consumer capabilities=%s worker_id=%s (claims use check-in claim_ready only)",
        capabilities, args.hostname,
    )
    while True:
        if state.get("current_job"):
            await asyncio.sleep(0.1)
            continue

        if not state.get("check_in_ok"):
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        ready = list(state.get("claim_ready_capabilities") or [])
        if not ready:
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        job = await ef_claim_any(http, ef_base, worker_key, ready, args.hostname)
        if job is None:
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        try:
            await process_ef_job(job, args, state, http, ef_base, worker_key)
        except Exception as ex:
            log.exception("process_ef_job outer crash: %s", ex)
            jid = str(job.get("job_id") or "")
            if jid:
                with contextlib.suppress(Exception):
                    await ef_fail(http, ef_base, worker_key, jid, str(ex))


async def ef_lora_sync_loop(args: argparse.Namespace, state: dict) -> None:
    """Startup + periodic pull of active LoRAs for this box's mode from loboforge.com."""
    interval = max(300, int(os.environ.get("LOBO_LORA_SYNC_INTERVAL", "1800")))
    while True:
        if agent.agent_fleet_busy(state):
            await asyncio.sleep(30.0)
            continue
        mode = resolve_lora_sync_mode(args.hostname)
        try:
            result = await asyncio.to_thread(sync_hub_active_loras, args, mode)
            await _sync_lora_inventory(state, args)
            log.info(
                "Periodic LoRA sync host=%s mode=%s pulled=%s skipped=%s known=%d",
                args.hostname,
                mode,
                result.get("pulled"),
                result.get("skipped"),
                len(state.get("known_loras") or []),
            )
        except Exception as ex:
            log.warning("Periodic LoRA sync failed host=%s mode=%s: %s", args.hostname, mode, ex)
        await asyncio.sleep(interval)


async def run_ef_agent(args: argparse.Namespace) -> None:
    ef_base = _resolve_ef_url()
    worker_key = os.environ.get("EVENT_FORGE_WORKER_KEY") or DEFAULT_WORKER_KEY
    capabilities = resolve_capabilities(args.hostname, os.environ.get("LOBO_MODE"))
    agent_state: dict[str, Any] = {
        "current_job": None,
        "known_loras": [],
        "models": {},
        "comfy_ok": True,
        "forge_queue_capabilities": capabilities,
    }

    models = await agent.load_models_with_retry(args)
    agent_state["known_loras"] = list(models.get("loras", []))
    agent_state["models"] = models

    try:
        from loboforge_worker.integration import start_sqs_background_provision
    except ImportError:
        start_sqs_background_provision = None  # type: ignore[misc, assignment]

    http_timeout = aiohttp.ClientTimeout(total=120)
    prov_task = await start_sqs_background_provision(args, agent_state) if start_sqs_background_provision else None
    async with aiohttp.ClientSession(timeout=http_timeout) as http:
        if not await ef_api_check_in_once(http, args, agent_state, ef_base, worker_key):
            log.warning("Initial EventForge check-in failed — will not claim until check-in succeeds")
        tasks = [
            ef_consumer_loop(capabilities, args, agent_state, http, ef_base, worker_key),
            sqs_lora_prefetch_loop(http, args, agent_state),
            ef_lora_sync_loop(args, agent_state),
            ef_api_check_in_loop(http, args, agent_state, ef_base, worker_key),
            agent.comfy_watchdog(args, agent_state, NoopSession()),
        ]
        if prov_task is not None:
            tasks.append(prov_task)
        await asyncio.gather(*tasks)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="LoboForge GPU Agent (EventForge)")
    p.add_argument("--secret", required=True)
    p.add_argument("--node-uuid", default=None)
    p.add_argument("--hostname", default=os.uname().nodename)
    p.add_argument("--capability", default=None)
    p.add_argument("--comfyui-http", default=agent.DEFAULT_COMFYUI_HTTP)
    p.add_argument("--comfyui-ws", default=agent.DEFAULT_COMFYUI_WS)
    p.add_argument("--ef-url", default=None, help="EventForge base URL override")
    p.add_argument("--debug", action="store_true")
    return p


def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )
    args = build_parser().parse_args()
    if args.debug:
        logging.getLogger().setLevel(logging.DEBUG)
    if args.capability:
        os.environ["FORGE_QUEUE_CAPABILITY"] = args.capability
    if args.ef_url:
        os.environ["EVENT_FORGE_URL"] = args.ef_url
    if not hasattr(args, "prefix"):
        args.prefix = ""
    if not getattr(args, "server", None):
        base = (os.environ.get("LOBO_BASE_URL") or "https://www.loboforge.com").strip().rstrip("/")
        args.server = base.replace("https://", "wss://").replace("http://", "ws://")

    uuid_file = Path("~/.loboforge_node_uuid").expanduser()
    if args.node_uuid:
        uuid_file.write_text(args.node_uuid)
    elif uuid_file.exists():
        args.node_uuid = uuid_file.read_text().strip()
    else:
        args.node_uuid = str(uuid.uuid4())
        uuid_file.write_text(args.node_uuid)

    caps = resolve_capabilities(args.hostname)
    ef = _resolve_ef_url()
    log.info(
        "GPU EventForge agent starting uuid=%s host=%s capabilities=%s ef=%s",
        args.node_uuid, args.hostname, caps, ef,
    )
    asyncio.run(run_ef_agent(args))


if __name__ == "__main__":
    main()
