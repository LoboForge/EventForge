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
    LOBO_LORA_SYNC_INTERVAL — seconds between idle LoRA reconciles (default 300)
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
    vision_models_allowed,
    CHECK_IN_INTERVAL,
    _lora_basenames_match,
    _normalize_lora_basename,
    _sync_lora_inventory,
    build_check_in_payload,
    comfy_inventory_stale_vs_disk,
    extract_required_loras_from_assign,
    is_disk_full_error,
    mark_disk_full,
    remove_invalid_lora_files,
    refresh_disk_guard,
    resolve_claim_ready_capabilities,
    resolve_lora_sync_mode,
    sync_hub_active_loras,
    pull_missing_loras_from_eventforge,
    pull_missing_loras_from_loboforge,
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
DISK_FULL_IDLE_SECS = float(os.environ.get("EVENT_FORGE_DISK_FULL_IDLE", "30"))
UPLOAD_RETRIES = int(os.environ.get("EVENT_FORGE_UPLOAD_RETRIES", "5"))
CLAIM_BLOCKED_HEAL_THRESHOLD = max(2, int(os.environ.get("EVENT_FORGE_CLAIM_BLOCKED_HEAL_THRESHOLD", "3")))
CLAIM_BLOCKED_HEAL_COOLDOWN = max(30.0, float(os.environ.get("EVENT_FORGE_CLAIM_BLOCKED_HEAL_COOLDOWN", "120")))


class EfGpuSession:
    """HTTP session for loboforge_agent.handle_job — EventForge uploads, no MQTT/SQS."""

    def __init__(
        self,
        args: argparse.Namespace,
        http: aiohttp.ClientSession,
        ef_base: str,
        worker_key: str,
        job_meta: dict[str, Any],
        agent_state: dict[str, Any] | None = None,
    ):
        self._args = args
        self._http = http
        self._ef = ef_base
        self._key = worker_key
        self._meta = job_meta
        self._agent_state = agent_state if agent_state is not None else {}
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
    # Always store as output.<ext> so fleet clients can resolve without listing S3.
    # (ComfyUI_NNNNN_.mp4 names previously broke ResolveOutputMedia which only
    # looked for output.mp4 / fixed names.)
    ext = Path(filename).suffix or ".bin"
    out_name = f"output{ext.lower()}" if ext else "output.bin"
    url = f"{session._ef}/v1/jobs/{job_uuid}/output?file={out_name}"
    headers = {
        "Authorization": f"Bearer {session._key}",
        "Content-Type": mime_type,
    }
    upload_timeout = aiohttp.ClientTimeout(total=600, sock_read=600)
    for attempt in range(1, UPLOAD_RETRIES + 1):
        try:
            if attempt > 1 and session._agent_state:
                await ef_api_check_in_once(
                    session._http, session._args, session._agent_state, session._ef, session._key
                )
            async with session._http.put(url, data=data, headers=headers, timeout=upload_timeout) as resp:
                if resp.status == 503:
                    retry_after = int(resp.headers.get("Retry-After", "5"))
                    log.warning(
                        "EventForge upload saturated job=%s attempt=%d retry=%ds",
                        job_uuid[:8], attempt, retry_after,
                    )
                    await asyncio.sleep(retry_after)
                    continue
                if resp.status == 404 and attempt < UPLOAD_RETRIES:
                    log.warning(
                        "Upload 404 job=%s attempt=%d — refreshing lease via check-in",
                        job_uuid[:8], attempt,
                    )
                    await asyncio.sleep(2)
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
    diagnostics: dict[str, Any] | None = None,
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
            if diagnostics is not None:
                def _header_int(name: str) -> int:
                    try:
                        return int(resp.headers.get(name, "0") or 0)
                    except (TypeError, ValueError):
                        return 0

                diagnostics.clear()
                diagnostics.update({
                    "queued_matching": _header_int("X-EventForge-Queued-Matching"),
                    "blocked_paused": _header_int("X-EventForge-Blocked-Paused"),
                    "blocked_model": _header_int("X-EventForge-Blocked-Model"),
                    "blocked_lora": _header_int("X-EventForge-Blocked-Lora"),
                    "missing_loras": [
                        name.strip()
                        for name in (resp.headers.get("X-EventForge-Missing-Loras") or "").split(",")
                        if name.strip()
                    ],
                })
            return None
        if resp.status == 401:
            raise RuntimeError("EventForge claim unauthorized — check EVENT_FORGE_WORKER_KEY")
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"EventForge claim HTTP {resp.status}: {text[:200]}")
        return await resp.json()


def _lora_is_indexed(state: dict, lora_name: str) -> bool:
    return any(
        _lora_basenames_match(indexed, lora_name)
        for indexed in (state.get("models") or {}).get("loras", [])
    )


async def _heal_lora_blocked_claims(
    args: argparse.Namespace,
    state: dict,
    diagnostics: dict[str, Any],
) -> None:
    """Repair the false-healthy state: claim-ready, idle, but every queued job is LoRA-blocked."""
    now = time.monotonic()
    last = float(state.get("_claim_blocked_last_heal") or 0)
    if now - last < CLAIM_BLOCKED_HEAL_COOLDOWN or state.get("_claim_blocked_healing"):
        return

    state["_claim_blocked_last_heal"] = now
    state["_claim_blocked_healing"] = True
    blocked = int(diagnostics.get("blocked_lora") or 0)
    missing = list(diagnostics.get("missing_loras") or [])
    state["claim_paused_reason"] = (
        f"{blocked} queued job(s) blocked by missing LoRAs; self-healing"
    )
    log.warning(
        "Claim-ready worker has %d LoRA-blocked queued job(s); syncing active LoRAs%s",
        blocked,
        f" ({', '.join(missing[:6])})" if missing else "",
    )

    try:
        removed = remove_invalid_lora_files(missing)
        if removed:
            log.warning("Removed corrupt/truncated LoRAs before self-heal: %s", ", ".join(removed))
        result = await asyncio.to_thread(
            sync_hub_active_loras,
            args,
            resolve_lora_sync_mode(args.hostname),
        )
        await _sync_lora_inventory(state, args)

        # Files may exist on disk while Comfy still serves a cached model list.
        # Restart only while idle, and only when the diagnostic names prove the
        # disk/index disagreement (or the sync just downloaded new files).
        disk_ready = bool(missing) and all(worker_has_lora(state, name) for name in missing)
        index_stale = disk_ready and any(not _lora_is_indexed(state, name) for name in missing)
        if (index_stale or int(result.get("pulled") or 0) > 0) and not state.get("current_job"):
            log.warning("LoRA files changed or Comfy inventory is stale — forcing idle ComfyUI re-index")
            if await agent.force_restart_comfyui(args):
                state["models"] = await agent.get_available_models(args.comfyui_http)
                await _sync_lora_inventory(state, args)

        failed = int(result.get("failed") or 0)
        unresolved = [name for name in missing if not worker_has_lora(state, name)]
        if failed or unresolved:
            state["claim_paused_reason"] = (
                f"LoRA self-heal incomplete: downloads_failed={failed}, "
                f"still_missing={len(unresolved)}"
            )
            log.warning(
                "LoRA claim self-heal incomplete — failed=%d unresolved=%s",
                failed,
                ", ".join(unresolved[:8]) or "none from diagnostic sample",
            )
        else:
            state.pop("claim_paused_reason", None)
            log.info("LoRA claim self-heal completed; claim inventory refreshed")
    except Exception as ex:
        state["claim_paused_reason"] = f"LoRA self-heal error: {type(ex).__name__}"
        log.warning("LoRA claim self-heal failed: %s", ex)
    finally:
        state["_claim_blocked_healing"] = False



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
        log.info(
            "Releasing job %s — model %s not runnable on this worker",
            job_id[:8],
            model_name,
        )
        await ef_release(http, ef_base, worker_key, job_id)
        return

    loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
    if not loras_ok:
        try:
            pulled_ef = await asyncio.to_thread(
                pull_missing_loras_from_eventforge,
                args,
                job_id,
                missing_loras,
                ef_base=ef_base,
                worker_key=worker_key,
            )
            if pulled_ef:
                log.info(
                    "Pre-job EventForge LoRA pull for %s: %s",
                    job_id[:8],
                    ", ".join(pulled_ef),
                )
                await _sync_lora_inventory(state, args)
                loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
        except OSError as ex:
            if ex.errno == 28 or is_disk_full_error(ex):
                mark_disk_full(state, "disk_full")
                log.error(
                    "Disk full during LoRA download for %s — releasing job and pausing claims",
                    job_id[:8],
                )
                await ef_release(http, ef_base, worker_key, job_id)
                return
            log.warning("Pre-job EventForge LoRA pull failed: %s", ex)
        except Exception as ex:
            log.warning("Pre-job EventForge LoRA pull failed: %s", ex)

    if not loras_ok:
        # LoboForge fallback — pull the *specific* missing LoRAs (not bulk mode sync alone).
        try:
            pulled_lf = await asyncio.to_thread(
                pull_missing_loras_from_loboforge,
                args,
                missing_loras,
            )
            if pulled_lf:
                log.info(
                    "Pre-job LoboForge LoRA pull for %s: %s",
                    job_id[:8],
                    ", ".join(pulled_lf),
                )
                await _sync_lora_inventory(state, args)
                loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
        except OSError as ex:
            if ex.errno == 28 or is_disk_full_error(ex):
                mark_disk_full(state, "disk_full")
                log.error(
                    "Disk full during LoboForge LoRA download for %s — releasing job and pausing claims",
                    job_id[:8],
                )
                await ef_release(http, ef_base, worker_key, job_id)
                return
            log.warning("Pre-job LoboForge LoRA pull failed: %s", ex)
        except Exception as ex:
            log.warning("Pre-job LoboForge LoRA pull failed: %s", ex)

    if not loras_ok:
        # Last resort: bulk active-loras sync for this box mode (may still miss tool-only LoRAs).
        try:
            sync_result = await asyncio.to_thread(sync_hub_active_loras, args)
            log.info(
                "Pre-job LoRA bulk sync mode=%s pulled=%s for missing: %s",
                sync_result.get("mode"),
                sync_result.get("pulled"),
                ", ".join(_normalize_lora_basename(l) for l in missing_loras),
            )
            await _sync_lora_inventory(state, args)
            loras_ok, missing_loras = await check_assign_job_loras_sqs(args, state, assign)
        except Exception as ex:
            log.warning("Pre-job LoRA bulk sync failed: %s", ex)

    if not loras_ok:
        missing_names = ", ".join(_normalize_lora_basename(l) for l in missing_loras)
        defer_counts: dict[str, int] = state.setdefault("_ef_lora_defer_counts", {})
        count = defer_counts.get(job_id, 0) + 1
        defer_counts[job_id] = count
        max_defers = int(os.environ.get("FORGE_QUEUE_MAX_LORA_DEFERS", "15"))
        if count >= max_defers:
            await ef_fail(http, ef_base, worker_key, job_id, f"missing LoRAs after {count} attempts: {missing_names}")
            return
        log.info(
            "Releasing job %s — refusing to run without LoRAs on disk: %s (attempt %d; EF then LoboForge tried)",
            job_id[:8], missing_names, count,
        )
        await ef_release(http, ef_base, worker_key, job_id)
        from loboforge_agent_sqs import _enqueue_lora_prefetch
        _enqueue_lora_prefetch(state, missing_loras, job_id=job_id)
        return

    state.get("_ef_lora_defer_counts", {}).pop(job_id, None)
    session = EfGpuSession(args, http, ef_base, worker_key, job, state)
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
        rl = reason.lower()
        if is_disk_full_error(reason):
            mark_disk_full(state, "disk_full")
            log.error(
                "Disk full during job %s — releasing and pausing claims: %s",
                job_id[:8], reason[:200],
            )
            await ef_release(http, ef_base, worker_key, job_id)
        elif reason.startswith("Text encoder not on worker"):
            log.info("Releasing job %s — %s", job_id[:8], reason)
            await ef_release(http, ef_base, worker_key, job_id)
        elif "not provisioned" in rl or "layout.json missing" in rl:
            log.warning("Releasing job %s — worker not ready: %s", job_id[:8], reason)
            await ef_release(http, ef_base, worker_key, job_id)
        elif (
            "prompt_outputs_failed_validation" in rl
            or "value_not_in_list" in rl
            or ("unet_name" in rl and "not in" in rl)
        ):
            # Incomplete Wan download / stale claim_ready: do not terminal-fail bulk backlog.
            log.warning(
                "Releasing job %s — Comfy model validation (refreshing claim_ready): %s",
                job_id[:8],
                reason[:240],
            )
            try:
                models = await agent.get_available_models(args.comfyui_http)
                state["models"] = models
            except Exception as ex:
                log.warning("Model refresh after validation failure failed: %s", ex)
            caps = state.get("forge_queue_capabilities") or []
            state["claim_ready_capabilities"] = resolve_claim_ready_capabilities(
                state, caps, hostname=args.hostname,
            )
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
        known: list[str] = []
        for name in models.get("loras") or []:
            base_name = _normalize_lora_basename(name)
            if (
                base_name
                and worker_has_lora(agent_state, base_name)
                and not any(_lora_basenames_match(k, base_name) for k in known)
            ):
                known.append(base_name)
        # Comfy's current inventory is authoritative. Appending forever preserves
        # deleted/truncated files and makes EventForge falsely consider jobs runnable.
        agent_state["known_loras"] = known
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
    empty_claims = 0
    claim_diagnostics: dict[str, Any] = {}
    while True:
        if state.get("current_job"):
            await asyncio.sleep(0.1)
            continue

        if not state.get("check_in_ok"):
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        # Disk headroom guard: never claim when nearly out of space, else one box
        # with "No space left on device" cascade-fails the whole queue.
        refresh_disk_guard(state)
        if state.get("disk_full"):
            hr = state.get("disk_headroom") or {}
            log.warning(
                "Skipping claim — disk full (free=%sMB, min=%sMB); waiting for space",
                hr.get("free_mb"), hr.get("min_free_mb"),
            )
            await asyncio.sleep(DISK_FULL_IDLE_SECS)
            continue

        ready = list(state.get("claim_ready_capabilities") or [])
        # Never claim caption/joycaption work — vision models are a privacy violation.
        if not vision_models_allowed():
            ready = [c for c in ready if c not in ("caption", "joycaption", "joy-caption")]
        if not ready:
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        try:
            job = await ef_claim_any(
                http,
                ef_base,
                worker_key,
                ready,
                args.hostname,
                claim_diagnostics,
            )
        except Exception as ex:
            log.warning("EventForge claim error: %s", ex)
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue
        if job is None:
            empty_claims += 1
            queued = int(claim_diagnostics.get("queued_matching") or 0)
            blocked_lora = int(claim_diagnostics.get("blocked_lora") or 0)
            if (
                queued > 0
                and blocked_lora > 0
                and empty_claims >= CLAIM_BLOCKED_HEAL_THRESHOLD
            ):
                await _heal_lora_blocked_claims(args, state, claim_diagnostics)
                empty_claims = 0
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue

        empty_claims = 0
        claim_diagnostics.clear()
        if str(state.get("claim_paused_reason") or "").startswith(
            ("LoRA self-heal", "queued job", "LoRA claim")
        ):
            state.pop("claim_paused_reason", None)
        try:
            await process_ef_job(job, args, state, http, ef_base, worker_key)
        except Exception as ex:
            log.exception("process_ef_job outer crash: %s", ex)
            jid = str(job.get("job_id") or "")
            if jid:
                with contextlib.suppress(Exception):
                    await ef_fail(http, ef_base, worker_key, jid, str(ex))


async def _prefetch_queued_eventforge_loras(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict,
    ef_base: str,
    worker_key: str,
) -> tuple[int, int]:
    """Download validated LoRAs for queued jobs before this worker can claim them."""
    caps = resolve_capabilities(args.hostname)
    params = {
        "hostname": args.hostname,
        "capabilities": ",".join(caps),
        "limit": "64",
    }
    headers = {"Authorization": f"Bearer {worker_key}"}
    async with http.get(
        f"{ef_base}/v1/workers/loras/needed",
        params=params,
        headers=headers,
        timeout=aiohttp.ClientTimeout(total=60),
    ) as resp:
        if resp.status == 404:
            # Rolling deploy compatibility: old servers do not expose prefetch.
            return 0, 0
        if resp.status != 200:
            raise RuntimeError(f"EventForge LoRA prefetch catalog HTTP {resp.status}")
        body = await resp.json()

    rows = body.get("loras") if isinstance(body, dict) else []
    if not isinstance(rows, list):
        return 0, 0

    pulled = 0
    failed = 0
    for row in rows:
        if not isinstance(row, dict):
            continue
        job_id = str(row.get("job_id") or "").strip()
        file_name = _normalize_lora_basename(str(row.get("file_name") or ""))
        if not job_id or not file_name or worker_has_lora(state, file_name):
            continue
        remove_invalid_lora_files([file_name])
        path = await asyncio.to_thread(
            pull_missing_loras_from_eventforge,
            args,
            job_id,
            [file_name],
            ef_base=ef_base,
            worker_key=worker_key,
        )
        if path:
            pulled += 1
            await _sync_lora_inventory(state, args)
        else:
            failed += 1

    return pulled, failed


async def ef_lora_sync_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict,
    ef_base: str,
    worker_key: str,
) -> None:
    """Continuously reconcile queued EF and hub LoRAs whenever this box is idle."""
    interval = max(60, int(os.environ.get("LOBO_LORA_SYNC_INTERVAL", "300")))
    while True:
        if agent.agent_fleet_busy(state):
            await asyncio.sleep(30.0)
            continue
        mode = resolve_lora_sync_mode(args.hostname)
        sleep_for = interval
        try:
            ef_pulled = 0
            ef_failed = 0
            try:
                ef_pulled, ef_failed = await _prefetch_queued_eventforge_loras(
                    http, args, state, ef_base, worker_key,
                )
            except Exception as ex:
                # EventForge catalog trouble must not suppress the independent
                # hub reconciliation path.
                ef_failed = 1
                log.warning("Queued EventForge LoRA prefetch failed: %s", ex)
            result = await asyncio.to_thread(sync_hub_active_loras, args, mode)
            await _sync_lora_inventory(state, args)
            failed = ef_failed + int(result.get("failed") or 0)
            if failed:
                # Informational only — the box keeps serving jobs it CAN do
                # (server routes LoRA jobs by known_loras); retry sooner.
                state["claim_paused_reason"] = (
                    f"Idle LoRA sync incomplete: {failed} download(s) failed"
                )
                sleep_for = min(interval, 60)
            elif str(state.get("claim_paused_reason") or "").startswith(
                ("Periodic LoRA sync", "Idle LoRA sync")
            ):
                state.pop("claim_paused_reason", None)
            log.info(
                "Idle LoRA sync host=%s mode=%s ef_pulled=%s hub_pulled=%s skipped=%s failed=%s known=%d",
                args.hostname,
                mode,
                ef_pulled,
                result.get("pulled"),
                result.get("skipped"),
                failed,
                len(state.get("known_loras") or []),
            )
        except Exception as ex:
            log.warning("Idle LoRA sync failed host=%s mode=%s: %s", args.hostname, mode, ex)
            sleep_for = min(interval, 60)
        await asyncio.sleep(sleep_for)


async def ef_claim_ready_reconcile_loop(
    args: argparse.Namespace,
    state: dict,
) -> None:
    """Recover claim_ready after model weights land on disk post-startup.

    Two systemic failures this fixes for video/wan boxes:
      1. The box inventories models at startup, *before* the 74GB Wan fp8 download
         finishes, so claim_ready starts empty. The 60s check-in loop re-queries
         Comfy, but ComfyUI caches its diffusion_models folder listing at boot —
         so a download that completes later is never indexed and claim_ready
         stays empty *forever* until a human restarts ComfyUI.
      2. Native Wan/LTX boxes whose layout/weights sync completes after startup.

    This loop re-inventories on a short cadence and, when Wan weights are present
    on disk but Comfy's UNET list is stale, force-restarts ComfyUI (idle only,
    rate-limited) so the download is indexed and claim_ready populates on its own.
    """
    interval = max(15.0, float(os.environ.get("EVENT_FORGE_REINDEX_INTERVAL", "45")))
    while True:
        await asyncio.sleep(interval)
        if state.get("current_job") or agent.agent_fleet_busy(state):
            continue

        caps = list(state.get("forge_queue_capabilities") or [])
        if not caps:
            continue
        ready_before = list(state.get("claim_ready_capabilities") or [])
        # Only intervene when the box *should* be serving something it currently isn't.
        missing = [c for c in caps if c not in ready_before]
        if not missing:
            continue

        try:
            models = await agent.get_available_models(args.comfyui_http)
            state["models"] = models
            known = state.setdefault("known_loras", [])
            for name in models.get("loras") or []:
                base_name = _normalize_lora_basename(name)
                if base_name and not any(_lora_basenames_match(k, base_name) for k in known):
                    known.append(base_name)
        except Exception as ex:
            log.debug("reconcile model query failed: %s", ex)
            continue

        # Weights on disk but Comfy's UNET cache is stale → re-index via restart.
        if comfy_inventory_stale_vs_disk(state, hostname=args.hostname):
            log.warning(
                "Wan weights present on disk but Comfy UNET inventory is stale — "
                "forcing ComfyUI re-index so claim_ready can populate",
            )
            if await agent.force_restart_comfyui(args):
                try:
                    state["models"] = await agent.get_available_models(args.comfyui_http)
                except Exception as ex:
                    log.debug("reconcile re-query after restart failed: %s", ex)

        ready_after = resolve_claim_ready_capabilities(state, caps, hostname=args.hostname)
        state["claim_ready_capabilities"] = ready_after
        if set(ready_after) != set(ready_before):
            log.info(
                "claim_ready reconciled: %s -> %s",
                ",".join(ready_before) or "none",
                ",".join(ready_after) or "none",
            )


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
    prov_task = None
    if start_sqs_background_provision:
        try:
            prov_task = await start_sqs_background_provision(args, agent_state)
        except Exception as ex:
            log.warning("Background provision start failed (continuing to claim): %s", ex)
    async with aiohttp.ClientSession(timeout=http_timeout) as http:
        if not await ef_api_check_in_once(http, args, agent_state, ef_base, worker_key):
            log.warning("Initial EventForge check-in failed — will not claim until check-in succeeds")
        tasks = [
            ef_consumer_loop(capabilities, args, agent_state, http, ef_base, worker_key),
            sqs_lora_prefetch_loop(http, args, agent_state),
            ef_lora_sync_loop(http, args, agent_state, ef_base, worker_key),
            ef_api_check_in_loop(http, args, agent_state, ef_base, worker_key),
            ef_claim_ready_reconcile_loop(args, agent_state),
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
