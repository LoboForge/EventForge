#!/usr/bin/env python3
"""
LoboForge Ollama Agent — EventForge transport
==============================================
Claims text_stream jobs from EventForge, streams Ollama /api/chat tokens back via
POST /v1/jobs/{id}/stream, then completes. Registers presence via EventForge check-in.

Env:
    EVENT_FORGE_URL       — default http://localhost:8090
    EVENT_FORGE_WORKER_KEY — default wrath-worker-key
    LOBO_SECRET           — worker auth
    OLLAMA_HOST           — local Ollama HTTP base

Requirements:
    pip install aiohttp boto3

Env (S3 payload hydration — same as forge-queue gen workers):
    FORGE_QUEUE_BUCKET / FORGE_QUEUE_REGION / AWS_* credentials
"""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any

import aiohttp

log = logging.getLogger("ollama-agent-ef")


def load_payload_from_s3(key: str) -> dict[str, Any] | None:
    """Load forge-queue chat payload (same S3 layout as gen / SQS dolphin jobs)."""
    bucket = os.environ.get("FORGE_QUEUE_BUCKET", "").strip()
    if not bucket:
        log.warning("FORGE_QUEUE_BUCKET unset — cannot load %s", key)
        return None
    try:
        import boto3
    except ImportError:
        log.warning("boto3 not installed — cannot load %s", key)
        return None
    region = (
        os.environ.get("FORGE_QUEUE_REGION")
        or os.environ.get("AWS_DEFAULT_REGION")
        or "us-east-2"
    ).strip()
    try:
        s3 = boto3.client("s3", region_name=region)
        obj = s3.get_object(Bucket=bucket, Key=key)
        data = json.loads(obj["Body"].read())
        return data if isinstance(data, dict) else None
    except Exception as ex:
        log.warning("S3 payload load failed for %s: %s", key, ex)
        return None


DEFAULT_EF = os.environ.get("EVENT_FORGE_URL", "http://localhost:8090").rstrip("/")
DEFAULT_WORKER_KEY = os.environ.get("EVENT_FORGE_WORKER_KEY", "wrath-worker-key")
DEFAULT_OLLAMA_URL = os.environ.get("OLLAMA_HOST", "http://127.0.0.1:11434")
PREFERRED_MODEL = os.environ.get("LOBO_OLLAMA_MODEL", "dolphin3:8b")
DEFAULT_NUM_CTX = int(os.environ.get("LOBO_OLLAMA_NUM_CTX", "32768"))
DEFAULT_NUM_PREDICT = int(os.environ.get("LOBO_NUM_PREDICT", "2048"))
CHECK_IN_INTERVAL = int(os.environ.get("LOBO_CHECK_IN_INTERVAL", "60"))
OLLAMA_STARTUP_TIMEOUT = 30
CAPABILITY = os.environ.get("EVENT_FORGE_CAPABILITY", "ollama-chat")
STREAM_BATCH_MS = int(os.environ.get("LOBO_TOKEN_BATCH_MS", "50"))
CLAIM_IDLE_SECS = float(os.environ.get("EVENT_FORGE_CLAIM_IDLE", "2"))


def normalize_ollama_url(raw: str) -> str:
    s = (raw or "127.0.0.1:11434").strip().rstrip("/")
    if s.startswith("http://") or s.startswith("https://"):
        return s.replace("0.0.0.0", "127.0.0.1")
    host = s.replace("0.0.0.0", "127.0.0.1")
    return f"http://{host}"


def resolve_hostname(explicit: str | None) -> str:
    if explicit and explicit.strip():
        return explicit.strip()
    label = (os.environ.get("LOBO_LABEL") or "loboforge-ollama").strip()
    inst = (os.environ.get("LOBO_INSTANCE_ID") or os.environ.get("CONTAINER_ID") or "").strip()
    if inst:
        suffix = f"-{inst}"
        return label if label.endswith(suffix) or label.endswith(inst) else f"{label}{suffix}"
    return os.uname().nodename


def resolve_http_base() -> str:
    raw = (os.environ.get("LOBO_BASE_URL") or os.environ.get("LOBOFORGE_AGENT_SERVER") or "").strip().rstrip("/")
    if raw.startswith("wss://"):
        return "https://" + raw[len("wss://") :]
    if raw.startswith("ws://"):
        return "http://" + raw[len("ws://") :]
    if raw.startswith("https://") or raw.startswith("http://"):
        return raw
    if raw:
        return "https://" + raw
    return "http://localhost:5250"


def resolve_model(requested: str, active_model: str, available: list[str]) -> str:
    req = (requested or "").strip()
    if not req:
        return active_model
    lower = req.lower()
    if lower.startswith("venice-") or lower.startswith("olafangensan"):
        return active_model
    if "roleplay" in lower or lower.startswith("loboforge-"):
        for name in available:
            if "roleplay" in name.lower() or name.lower().startswith("loboforge-"):
                return name
        return active_model
    if lower.startswith("dolphin"):
        for name in available:
            if "dolphin" in name.lower():
                return name
        return active_model
    if req in available:
        return req
    base = req.split(":")[0]
    for name in available:
        if name.split(":")[0] == base:
            return name
    return active_model


async def is_ollama_running(ollama_url: str) -> bool:
    try:
        async with aiohttp.ClientSession() as session:
            async with session.get(
                f"{ollama_url}/api/tags",
                timeout=aiohttp.ClientTimeout(total=3),
            ) as r:
                return r.ok
    except Exception:
        return False


async def ensure_ollama_running(ollama_url: str) -> None:
    if await is_ollama_running(ollama_url):
        log.info("Ollama is already running.")
        return
    log.info("Starting Ollama...")
    subprocess.Popen(
        ["ollama", "serve"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        start_new_session=True,
    )
    deadline = time.monotonic() + OLLAMA_STARTUP_TIMEOUT
    while time.monotonic() < deadline:
        if await is_ollama_running(ollama_url):
            log.info("Ollama started.")
            return
        await asyncio.sleep(0.5)
    raise RuntimeError(f"Ollama did not start within {OLLAMA_STARTUP_TIMEOUT}s")


async def get_ollama_models(ollama_url: str) -> list[str]:
    async with aiohttp.ClientSession() as session:
        async with session.get(f"{ollama_url}/api/tags") as r:
            r.raise_for_status()
            data = await r.json()
    return [m.get("name", "") for m in data.get("models", []) if m.get("name")]


async def ensure_model_available(ollama_url: str, preferred: str) -> str:
    models = await get_ollama_models(ollama_url)
    if preferred in models:
        return preferred
    for name in models:
        if preferred.split(":")[0] in name:
            return name
    log.info("Pulling model %s...", preferred)
    proc = await asyncio.create_subprocess_exec("ollama", "pull", preferred)
    await proc.wait()
    models = await get_ollama_models(ollama_url)
    if preferred in models:
        return preferred
    if models:
        return models[0]
    raise RuntimeError(f"No Ollama models available after pull {preferred}")


class EventForgeClient:
    def __init__(self, base_url: str, worker_key: str, session: aiohttp.ClientSession):
        self.base = base_url.rstrip("/")
        self.key = worker_key
        self.session = session
        self.headers = {
            "Authorization": f"Bearer {worker_key}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        }

    async def claim(self, capabilities: list[str], worker_id: str) -> dict[str, Any] | None:
        async with self.session.post(
            f"{self.base}/v1/jobs/claim",
            json={"capabilities": capabilities, "hostname": worker_id},
            headers=self.headers,
            timeout=aiohttp.ClientTimeout(total=30),
        ) as r:
            if r.status == 204:
                return None
            if not r.ok:
                body = await r.text()
                raise RuntimeError(f"claim failed HTTP {r.status}: {body[:200]}")
            return await r.json()

    async def push_stream(self, job_id: str, delta: str) -> None:
        async with self.session.post(
            f"{self.base}/v1/jobs/{job_id}/stream",
            json={"delta": delta},
            headers=self.headers,
            timeout=aiohttp.ClientTimeout(total=30),
        ) as r:
            if not r.ok:
                body = await r.text()
                raise RuntimeError(f"stream failed HTTP {r.status}: {body[:200]}")

    async def complete(self, job_id: str, text: str) -> None:
        async with self.session.post(
            f"{self.base}/v1/jobs/{job_id}/complete",
            json={"text": text},
            headers=self.headers,
            timeout=aiohttp.ClientTimeout(total=30),
        ) as r:
            if not r.ok:
                body = await r.text()
                raise RuntimeError(f"complete failed HTTP {r.status}: {body[:200]}")

    async def fail(self, job_id: str, error: str) -> None:
        """Ollama/dolphin jobs must never terminal-fail — release back to queue."""
        await self.release(job_id, reason=error)

    async def release(self, job_id: str, *, reason: str | None = None) -> None:
        async with self.session.post(
            f"{self.base}/v1/jobs/{job_id}/release",
            headers=self.headers,
            timeout=aiohttp.ClientTimeout(total=30),
        ) as r:
            if not r.ok:
                body = await r.text()
                log.warning("release POST HTTP %s: %s", r.status, body[:200])
            elif reason:
                log.warning("Released job %s back to queue: %s", job_id[:8], reason[:200])


class StreamWriter:
    def __init__(self, ef: EventForgeClient, job_id: str):
        self._ef = ef
        self._job_id = job_id
        self._buf: list[str] = []
        self._last_flush = time.monotonic()
        self.full_text: list[str] = []

    async def append(self, token: str) -> None:
        if not token:
            return
        self.full_text.append(token)
        self._buf.append(token)
        batch_secs = max(0.0, STREAM_BATCH_MS / 1000.0)
        if sum(len(x) for x in self._buf) >= 48 or (time.monotonic() - self._last_flush) >= batch_secs:
            await self.flush()

    async def flush(self) -> None:
        if not self._buf:
            return
        text = "".join(self._buf)
        self._buf = []
        self._last_flush = time.monotonic()
        await self._ef.push_stream(self._job_id, text)


async def run_chat_job(
    payload: dict[str, Any],
    job_id: str,
    args: argparse.Namespace,
    ef: EventForgeClient,
    active_model: str,
    models: list[str],
) -> tuple[str, str | None]:
    request_id = str(payload.get("request_id") or job_id)
    requested = str(payload.get("model") or "")
    model = resolve_model(requested, active_model, models)
    messages = payload.get("messages") or []
    temperature = float(payload.get("temperature", 0.7))
    num_ctx = int(payload.get("num_ctx") or DEFAULT_NUM_CTX)
    num_predict = int(payload.get("num_predict") or DEFAULT_NUM_PREDICT)

    log.info("[%s] chat_request model=%s messages=%d", request_id[:8], model, len(messages))
    writer = StreamWriter(ef, job_id)

    if args.simulate:
        for tok in ["[sim] ", "The ", "wolf ", "waits ", "in ", "the ", "dark."]:
            await writer.append(tok)
            await asyncio.sleep(0.02)
        await writer.flush()
        return "completed", "".join(writer.full_text)

    body = {
        "model": model,
        "messages": messages,
        "stream": True,
        "options": {
            "temperature": temperature,
            "num_ctx": num_ctx,
            "num_predict": num_predict,
        },
    }

    async with aiohttp.ClientSession() as ollama_session:
        async with ollama_session.post(
            f"{args.ollama_url}/api/chat",
            json=body,
            timeout=aiohttp.ClientTimeout(total=None, connect=10),
        ) as r:
            if not r.ok:
                err_body = await r.text()
                return "failed", f"Ollama HTTP {r.status}: {err_body[:200]}"
            async for line in r.content:
                line = line.strip()
                if not line:
                    continue
                try:
                    chunk = json.loads(line)
                except json.JSONDecodeError:
                    continue
                token = chunk.get("message", {}).get("content", "") or chunk.get("response", "")
                if token:
                    await writer.append(token)
                if chunk.get("done", False):
                    break

    await writer.flush()
    return "completed", "".join(writer.full_text)


async def process_job(
    job: dict[str, Any],
    args: argparse.Namespace,
    ef: EventForgeClient,
    state: dict[str, Any],
    active_model: str,
    models: list[str],
) -> None:
    job_id = job["job_id"]
    kind = (job.get("kind") or "").strip()
    if kind != "text_stream":
        await ef.fail(job_id, f"Unsupported job kind: {kind or 'missing'}")
        return

    state["current_job"] = job_id
    try:
        raw = job.get("payload") or {}
        if not isinstance(raw, dict):
            await ef.fail(job_id, "Invalid payload (not object)")
            return

        job_type = str(raw.get("type") or "")
        if job_type != "chat_request":
            inner = raw.get("payload")
            if isinstance(inner, dict) and inner.get("type") == "chat_request":
                raw = inner
            else:
                await ef.fail(job_id, f"Unsupported payload type: {job_type or 'missing'}")
                return

        # LoboForge enqueues `{ type, request_id, payload_key }` with messages in S3.
        payload_key = str(raw.get("payload_key") or "").strip()
        if payload_key and not raw.get("messages"):
            loaded = await asyncio.to_thread(load_payload_from_s3, payload_key)
            if not isinstance(loaded, dict):
                await ef.fail(job_id, f"Failed to load roleplay payload from S3 ({payload_key})")
                return
            raw = loaded
            log.info(
                "[%s] hydrated payload_key=%s messages=%d",
                job_id[:8],
                payload_key,
                len(raw.get("messages") or []) if isinstance(raw.get("messages"), list) else 0,
            )

        status, result = await run_chat_job(raw, job_id, args, ef, active_model, models)
        if status == "failed":
            await ef.release(job_id, reason=result or "Ollama error")
            log.warning("[%s] released (was error): %s", job_id[:8], result)
        else:
            await ef.complete(job_id, result or "")
            log.info("[%s] Done (%d chars)", job_id[:8], len(result or ""))
    except Exception as ex:
        log.exception("Job %s error — releasing back to queue", job_id[:8])
        await ef.release(job_id, reason=str(ex))
    finally:
        state["current_job"] = None


async def claim_loop(
    args: argparse.Namespace,
    state: dict[str, Any],
    ef: EventForgeClient,
    active_model: str,
    models: list[str],
) -> None:
    log.info(
        "EventForge consumer capability=%s worker_id=%s ef=%s (server-side priority)",
        args.capability, args.hostname, args.ef_url,
    )
    while True:
        if state.get("current_job"):
            await asyncio.sleep(0.1)
            continue
        try:
            job = await ef.claim([args.capability], args.hostname)
        except Exception as ex:
            log.warning("claim error: %s", ex)
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue
        if job is None:
            await asyncio.sleep(CLAIM_IDLE_SECS)
            continue
        await process_job(job, args, ef, state, active_model, models)


def build_check_in_payload(args: argparse.Namespace, state: dict[str, Any]) -> dict[str, Any]:
    cap = args.capability
    return {
        "node_uuid": args.node_uuid,
        "hostname": args.hostname,
        "agent_type": "ollama",
        "ollama_url": args.ollama_url,
        "ollama_models": state.get("ollama_models") or [args.preferred_model],
        "busy": bool(state.get("current_job")),
        "current_job_uuid": state.get("current_job"),
        "comfy_ok": True,
        "forge_queue_capabilities": [cap],
        "claim_ready_capabilities": [cap],
    }


def build_lobo_check_in_payload(args: argparse.Namespace, state: dict[str, Any]) -> dict[str, Any]:
    cap = args.capability
    return {
        "node_uuid": args.node_uuid,
        "hostname": args.hostname,
        "transport": "eventforge",
        "forge_queue_capability": cap,
        "provision_mode": "ollama",
        "fleet_mode": "ollama",
        "ollama_url": args.ollama_url,
        "ollama_models": state.get("ollama_models") or [args.preferred_model],
        "busy": bool(state.get("current_job")),
        "current_job_uuid": state.get("current_job"),
        "comfy_ok": True,
    }


async def lobo_check_in_once(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict[str, Any],
) -> bool:
    if not args.secret:
        return False
    url = f"{resolve_http_base()}/api/agent/check-in"
    headers = {
        "Authorization": f"Bearer {args.secret}",
        "Content-Type": "application/json",
    }
    payload = build_lobo_check_in_payload(args, state)
    try:
        async with http.post(url, json=payload, headers=headers) as resp:
            if resp.status != 200:
                body = await resp.text()
                log.warning("LoboForge check-in failed HTTP %s: %s", resp.status, body[:200])
                return False
            log.info("LoboForge check-in OK (OllamaNodes presence)")
            return True
    except Exception as ex:
        log.warning("LoboForge check-in error: %s", ex)
        return False


async def lobo_check_in_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict[str, Any],
) -> None:
    await lobo_check_in_once(http, args, state)
    while True:
        await asyncio.sleep(CHECK_IN_INTERVAL)
        await lobo_check_in_once(http, args, state)


async def api_check_in_once(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict[str, Any],
) -> bool:
    url = f"{args.ef_url.rstrip('/')}/v1/workers/check-in"
    headers = {
        "Authorization": f"Bearer {args.worker_key}",
        "Content-Type": "application/json",
    }
    payload = build_check_in_payload(args, state)
    payload["transport"] = "eventforge"
    try:
        async with http.post(url, json=payload, headers=headers) as resp:
            if resp.status != 200:
                body = await resp.text()
                log.warning("EventForge check-in failed HTTP %s: %s", resp.status, body[:200])
                return False
            data = await resp.json()
            ack = data.get("acknowledged_models") or []
            log.info("EventForge check-in OK — %d models acknowledged", len(ack))
            return True
    except Exception as ex:
        log.warning("EventForge check-in error: %s", ex)
        return False


async def api_check_in_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict[str, Any],
) -> None:
    await api_check_in_once(http, args, state)
    while True:
        await asyncio.sleep(CHECK_IN_INTERVAL)
        await api_check_in_once(http, args, state)


async def run_agent(args: argparse.Namespace) -> None:
    if args.simulate:
        active_model = args.preferred_model
        models = [args.preferred_model]
    else:
        await ensure_ollama_running(args.ollama_url)
        active_model = await ensure_model_available(args.ollama_url, args.preferred_model)
        models = await get_ollama_models(args.ollama_url)
        if active_model in models:
            models.remove(active_model)
        models.insert(0, active_model)

    state: dict[str, Any] = {
        "current_job": None,
        "ollama_models": models,
    }

    timeout = aiohttp.ClientTimeout(total=300)
    async with aiohttp.ClientSession(timeout=timeout) as session:
        ef = EventForgeClient(args.ef_url, args.worker_key, session)
        check_http = aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=45))
        try:
            await asyncio.gather(
                claim_loop(args, state, ef, active_model, models),
                api_check_in_loop(check_http, args, state),
                lobo_check_in_loop(check_http, args, state),
            )
        finally:
            await check_http.close()


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="LoboForge Ollama Agent (EventForge)")
    p.add_argument("--ef-url", default=DEFAULT_EF)
    p.add_argument("--worker-key", default=DEFAULT_WORKER_KEY)
    p.add_argument("--capability", default=CAPABILITY)
    p.add_argument("--secret", default=os.environ.get("LOBO_SECRET", os.environ.get("LOBOFORGE_AGENT_SECRET", "")))
    p.add_argument("--node-uuid", default=None)
    p.add_argument("--hostname", default=None)
    p.add_argument("--ollama-url", default=DEFAULT_OLLAMA_URL)
    p.add_argument("--preferred-model", default=PREFERRED_MODEL)
    p.add_argument("--simulate", action="store_true", help="Skip Ollama; emit canned stream (dev)")
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
    if not args.secret:
        log.warning("No --secret / LOBO_SECRET — API check-in will fail (roleplay may still work if node cached)")

    uuid_file = Path("~/.loboforge_ollama_node_uuid").expanduser()
    if args.node_uuid:
        uuid_file.write_text(args.node_uuid)
    elif uuid_file.exists():
        args.node_uuid = uuid_file.read_text().strip()
    else:
        args.node_uuid = str(uuid.uuid4())
        uuid_file.write_text(args.node_uuid)

    args.hostname = resolve_hostname(args.hostname)

    args.ollama_url = normalize_ollama_url(args.ollama_url)
    args.ef_url = args.ef_url.rstrip("/")

    log.info(
        "EventForge Ollama agent starting uuid=%s host=%s model=%s ef=%s cap=%s",
        args.node_uuid,
        args.hostname,
        args.preferred_model,
        args.ef_url,
        args.capability,
    )
    asyncio.run(run_agent(args))


if __name__ == "__main__":
    main()
