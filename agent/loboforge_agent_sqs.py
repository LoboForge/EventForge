#!/usr/bin/env python3
"""
LoboForge GPU Agent — forge-queue SQS transport
================================================
Polls forge-queue SQS capability×tier queues, runs Comfy/native jobs, writes S3
manifests. No MQTT. No IoT certs. IAM credentials for SQS/S3.

LoRA prefetch uses HTTP POST /api/agent/request-work (download_lora or empty only —
never assign_job; gen jobs come exclusively from forge-queue SQS). When a claimed
job needs LoRAs that are not on disk, the worker defers that message immediately,
queues the missing names for background download, and keeps polling/running other jobs.

Env:
    FORGE_QUEUE_REGION, FORGE_QUEUE_BUCKET, FORGE_QUEUE_PREFIX
    FORGE_QUEUE_CAPABILITY — override capability (comma-list for multi-poll)
    AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY (or instance role)
    LOBO_BASE_URL — hub for check-in, ref images, request-work
    LOBO_SECRET — worker auth
    FORGE_QUEUE_VISIBILITY — processing lease seconds (default 300)
    FORGE_QUEUE_RECEIVE_VISIBILITY — initial receive lease seconds (default 45)
    FORGE_QUEUE_DEFER_SECS — short defer for LoRA prefetch (default 60)
    FORGE_QUEUE_MAX_LORA_DEFERS — fail after N LoRA defers per job (default 15)
    FORGE_QUEUE_MAX_TOTAL_DEFERS — fail after N LoRA defers fleet-wide (default 30)
    FORGE_QUEUE_HOLD_WARN_SECS — log when invisible lease held longer (default 120)
    FORGE_QUEUE_MAX_IDLE_SECS — max seconds between jobs when queue empty (default 2)
    FORGE_QUEUE_IDLE_SLEEP — extra sleep after empty poll (default 0; poll already waits)

Requirements:
    pip install -e forge-queue/sdk
    pip install aiohttp boto3
"""

from __future__ import annotations

import argparse
import contextlib
import asyncio
import json
import logging
import os
import sys
import time
import uuid
from pathlib import Path
from typing import Any

import aiohttp

import loboforge_agent as agent

# Lazy-loaded — EventForge workers import this module for LoRA/capability helpers only.
ForgeQueueConfig: Any = None
JobEnvelope: Any = None
OutputFile: Any = None
ForgeQueueWorker: Any = None
utc_now_iso: Any = None
_FORGE_QUEUE_SDK: bool | None = None


def _load_forge_queue_sdk() -> bool:
    global ForgeQueueConfig, JobEnvelope, OutputFile, ForgeQueueWorker, utc_now_iso, _FORGE_QUEUE_SDK
    if _FORGE_QUEUE_SDK is not None:
        return _FORGE_QUEUE_SDK
    try:
        from forge_queue.config import ForgeQueueConfig as _ForgeQueueConfig
        from forge_queue.types import JobEnvelope as _JobEnvelope, OutputFile as _OutputFile, utc_now_iso as _utc_now_iso
        from forge_queue.worker import ForgeQueueWorker as _ForgeQueueWorker

        ForgeQueueConfig = _ForgeQueueConfig
        JobEnvelope = _JobEnvelope
        OutputFile = _OutputFile
        ForgeQueueWorker = _ForgeQueueWorker
        utc_now_iso = _utc_now_iso
        _FORGE_QUEUE_SDK = True
    except ImportError:
        _FORGE_QUEUE_SDK = False
    return _FORGE_QUEUE_SDK


def _require_forge_queue_sdk() -> None:
    if not _load_forge_queue_sdk():
        raise RuntimeError(
            "forge-queue SDK not installed — run: pip install -e forge-queue/sdk"
        )

from loboforge_agent_common import (
    CHECK_IN_INTERVAL,
    _normalize_lora_basename,
    _sync_lora_inventory,
    build_check_in_payload,
    extract_required_loras_from_assign,
    worker_can_poll_capability,
    worker_can_run_assign,
    worker_has_lora,
)

log = logging.getLogger("gpu-agent-sqs")

REQUEST_WORK_PATH = "/api/agent/request-work"

# User-facing tiers drain first; bulk (dataset prep / FleetLoboRemover) only when idle.
FORGE_PRIORITY_TIERS = ("admin", "vip", "normal")
FORGE_BULK_TIERS = ("bulk",)

_RETRYABLE_AWS_CODES = frozenset({
    "QueueDoesNotExist",
    "AWS.SimpleQueueService.NonExistentQueue",
    "NoSuchBucket",
    "NoSuchKey",
})


def _retryable_aws_error(exc: BaseException) -> bool:
    """True when forge-queue infra is not deployed yet (queue/bucket missing)."""
    try:
        from botocore.exceptions import ClientError
    except ImportError:
        return False
    if isinstance(exc, ClientError):
        code = exc.response.get("Error", {}).get("Code", "")
        return code in _RETRYABLE_AWS_CODES
    cause = getattr(exc, "__cause__", None)
    return isinstance(cause, ClientError) and _retryable_aws_error(cause)


def resolve_http_base() -> str:
    raw = (os.environ.get("LOBO_BASE_URL") or agent.DEFAULT_SERVER).strip().rstrip("/")
    if raw.startswith("wss://"):
        return "https://" + raw[len("wss://") :]
    if raw.startswith("ws://"):
        return "http://" + raw[len("ws://") :]
    if raw.startswith("https://") or raw.startswith("http://"):
        return raw
    return "https://" + raw


def _env_flag(name: str, default: bool) -> bool:
    raw = (os.environ.get(name) or "").strip().lower()
    if not raw:
        return default
    return raw not in ("0", "false", "no", "off")


try:
    from loboforge_worker.capabilities import forge_queue_capabilities_for_mode as capabilities_for_mode
except ImportError:
    def capabilities_for_mode(mode: str | None = None) -> tuple[str, ...]:  # noqa: F811
        """Fallback when loboforge_worker package is not on PYTHONPATH."""
        mode_l = (mode or os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "all").strip().lower()
        if "," in mode_l:
            mode_l = mode_l.split(",")[0].strip()
        if mode_l == "both":
            mode_l = "all"
        wan = _env_flag("LOBO_WAN", mode_l not in ("image", "music"))
        ltx23 = _env_flag("LOBO_LTX23", mode_l in ("all", "ltx-native", "ltx"))
        music = _env_flag("LOBO_MUSIC", mode_l not in ("image",))
        if mode_l == "image":
            return ("flux-klein", "flux-klein-edit", "zimage", "chroma")
        if mode_l == "video":
            caps: list[str] = []
            if wan:
                caps.append("wan")
            if ltx23:
                caps.append("ltx")
            return tuple(caps or ("wan",))
        if mode_l == "music":
            return ("wan",)
        if mode_l == "all":
            caps = ["flux-klein", "flux-klein-edit", "zimage", "chroma"]
            if wan:
                caps.append("wan")
            if ltx23:
                caps.append("ltx")
            return tuple(caps)
        if mode_l in ("ltx-native", "ltx"):
            return ("ltx",)
        if mode_l == "dolphin":
            return ("dolphin",)
        return ("flux-klein",)


def resolve_capability(hostname: str | None = None, mode: str | None = None) -> str:
    """Primary forge-queue capability (first of the resolved set)."""
    caps = resolve_capabilities(hostname, mode)
    return caps[0] if caps else "flux-klein"


def resolve_capabilities(hostname: str | None = None, mode: str | None = None) -> tuple[str, ...]:
    explicit = (os.environ.get("FORGE_QUEUE_CAPABILITY") or os.environ.get("FORGE_QUEUE_CAPABILITIES") or "").strip()
    mode_l = (mode or os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()

    if explicit:
        if explicit.lower() in ("both", "all"):
            return capabilities_for_mode(mode_l or "all")
        return tuple(c.strip() for c in explicit.split(",") if c.strip())

    hn = (hostname or "").lower()
    if "caption" in hn or "joycaption" in hn:
        return ("caption",)
    if "dolphin" in hn or "ollama" in hn:
        return ("dolphin",)
    if hn.startswith("loboforge-ltx") or (hn.startswith("loboforge-") and "-ltx-" in hn):
        return ("ltx",)

    if mode_l:
        return capabilities_for_mode(mode_l)

    if "-video-" in hn or "-wan-" in hn or hn.endswith("-wan"):
        return capabilities_for_mode("video")
    if "ltx" in hn:
        return ("ltx",)
    if "chroma" in hn:
        return ("chroma",)
    if "zimage" in hn or "lens" in hn:
        return ("zimage",)
    if "-image-" in hn:
        return capabilities_for_mode("image")
    return capabilities_for_mode("all")


class NoopSession:
    async def send_json(self, data: dict) -> bool:
        return True


class SqsGpuSession:
    """Minimal session for loboforge_agent.handle_job — S3 uploads, no MQTT."""

    def __init__(
        self,
        args: argparse.Namespace,
        config: ForgeQueueConfig,
        envelope: JobEnvelope,
    ):
        self._args = args
        self._config = config
        self._envelope = envelope
        self._uuid = args.node_uuid
        self._current_job: str | None = None
        self.uploaded_outputs: list[tuple[str, str]] = []
        self.last_wd14: dict | None = None
        self.failed_reason: str | None = None
        self.hostname = args.hostname

    def set_current_job(self, job_uuid: str | None) -> None:
        self._current_job = job_uuid

    async def send_json(self, data: dict, topic: str | None = None) -> bool:
        mtype = data.get("type", "")
        job = data.get("job_uuid") or self._current_job or ""
        if mtype == "job_started" and job:
            await asyncio.to_thread(
                _write_started_json, self._config, job, self._args, self._envelope,
            )
            return True
        if mtype == "job_failed" and job:
            self.failed_reason = str(data.get("reason") or "worker failure")
            return True
        return True

    async def wait_response(self, types: set[str], timeout: float) -> dict:
        if "file_ready" in types:
            return {"type": "file_ready"}
        if "file_ok" in types:
            return {"type": "file_ok"}
        raise asyncio.TimeoutError()

    async def send_bytes(self, data: bytes) -> None:
        raise NotImplementedError("SQS agent uses S3 upload")

    async def send(self, raw: str) -> None:
        try:
            data = json.loads(raw)
        except json.JSONDecodeError:
            return
        if data.get("type") == "download_error":
            log.warning("LoRA download error: %s", data.get("reason"))


def _write_started_json(
    config: ForgeQueueConfig,
    job_id: str,
    args: argparse.Namespace,
    envelope: JobEnvelope,
) -> None:
    bucket = config.ensure_bucket()
    key = f"results/{job_id}/started.json"
    body = json.dumps(
        {
            "job_uuid": job_id,
            "node_uuid": args.node_uuid,
            "hostname": args.hostname,
            "tenant_id": envelope.tenant_id or "",
            "started_at": utc_now_iso(),
        },
        separators=(",", ":"),
    ).encode("utf-8")
    config.s3().put_object(Bucket=bucket, Key=key, Body=body, ContentType="application/json")
    log.info("Wrote started s3://%s/%s", bucket, key)


async def send_file_s3(
    session: SqsGpuSession,
    config: ForgeQueueConfig,
    job_uuid: str,
    filename: str,
    data: bytes,
    mime_type: str,
) -> bool:
    wd14 = await agent._tag_image_async(data, mime_type)
    ext = Path(filename).suffix or ".bin"
    out_key = f"results/{job_uuid}/output{ext}"
    bucket = config.ensure_bucket()
    await asyncio.to_thread(
        config.s3().put_object,
        Bucket=bucket,
        Key=out_key,
        Body=data,
        ContentType=mime_type,
    )
    session.uploaded_outputs.append((out_key, mime_type))
    if wd14 is not None:
        session.last_wd14 = wd14
    log.info("Uploaded output s3://%s/%s (%d bytes)", bucket, out_key, len(data))
    return True


async def accept_assign_job_sqs(
    session: SqsGpuSession,
    msg: dict,
    args,
    state: dict,
) -> None:
    job_id = msg.get("job_uuid")
    session.set_current_job(job_id)
    state["current_job"] = job_id
    state["current_job_since"] = time.time()
    state["job_started"] = False
    original_send_file = agent.send_file

    async def _send_file_wrapped(ws, job_uuid, filename, data, mime_type):
        return await send_file_s3(session, session._config, job_uuid, filename, data, mime_type)

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


def _extract_assign_payload(raw: dict[str, Any]) -> dict | None:
    if not isinstance(raw, dict):
        return None
    if raw.get("type") == "assign_job":
        return raw
    inner = raw.get("payload")
    if isinstance(inner, dict) and inner.get("type") == "assign_job":
        out = dict(inner)
        if raw.get("job_uuid") and not out.get("job_uuid"):
            out["job_uuid"] = raw["job_uuid"]
        return out
    if isinstance(inner, dict):
        return inner
    return None


async def api_request_work_download(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict,
    *,
    timeout: float = 120.0,
) -> str | None:
    lock = state.setdefault("_request_work_lock", asyncio.Lock())
    async with lock:
        url = f"{resolve_http_base()}{REQUEST_WORK_PATH}"
        headers = {
            "Authorization": f"Bearer {args.secret}",
            "Content-Type": "application/json",
        }
        payload = {
            "type": "request_work",
            "node_uuid": args.node_uuid,
            "hostname": args.hostname,
            "vram_free": agent.get_vram_free(),
            "disk_free_mb": agent.get_disk_free_mb(),
            "known_loras": state.get("known_loras", []),
        }
        try:
            async with http.post(
                url, json=payload, headers=headers, timeout=aiohttp.ClientTimeout(total=timeout),
            ) as resp:
                if resp.status == 404:
                    return None
                if resp.status != 200:
                    return None
                msg = await resp.json(content_type=None)
        except (asyncio.TimeoutError, Exception):
            return None

        mtype = (msg or {}).get("type", "")
        if mtype == "assign_job":
            log.error(
                "API request-work returned assign_job — misconfigured hub; "
                "SQS workers must receive jobs from forge-queue only (ignoring)"
            )
            return None
        if mtype == "download_lora":
            _require_forge_queue_sdk()
            stub = SqsGpuSession(
                args,
                ForgeQueueConfig.from_env(),
                JobEnvelope(job_id="prefetch", capability="", tier="", payload_s3=""),
            )
            await agent.handle_download_model(stub, msg, args)
            models = await agent.get_available_models(args.comfyui_http)
            state["models"] = models
            basename = msg.get("lora_basename") or Path(msg.get("dest_path", "")).name
            if basename:
                known = state.setdefault("known_loras", [])
                if basename not in known:
                    known.append(basename)
        return mtype or None


def _enqueue_lora_prefetch(state: dict, missing: list[str]) -> None:
    """Track LoRAs a deferred job needs — background loop will fetch via request-work."""
    wanted: set[str] = state.setdefault("_lora_prefetch_wanted", set())
    for name in missing:
        base = _normalize_lora_basename(name)
        if base:
            wanted.add(base)


def _reconcile_lora_prefetch_wanted(state: dict) -> None:
    wanted: set[str] = state.get("_lora_prefetch_wanted") or set()
    if not wanted:
        return
    state["_lora_prefetch_wanted"] = {
        w for w in wanted if not worker_has_lora(state, w)
    }


async def check_assign_job_loras_sqs(
    args,
    state: dict,
    payload: dict,
) -> tuple[bool, list[str]]:
    """Fast local check — does not block on download (see sqs_lora_prefetch_loop)."""
    required = extract_required_loras_from_assign(payload)
    if not required:
        return True, []
    await _sync_lora_inventory(state, args)
    missing = [l for l in required if not worker_has_lora(state, l)]
    return not missing, missing


async def sqs_lora_prefetch_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    state: dict,
) -> None:
    """Background LoRA download via request-work — runs while other jobs execute."""
    backoff = 30.0
    while True:
        wanted: set[str] = state.get("_lora_prefetch_wanted") or set()
        fleet_busy = agent.agent_fleet_busy(state)
        if not wanted and fleet_busy:
            await asyncio.sleep(0.5)
            continue
        if state.get("_lora_prefetch_downloading"):
            await asyncio.sleep(0.5)
            continue

        state["_lora_prefetch_downloading"] = True
        try:
            if wanted:
                try:
                    from loboforge_agent_common import resolve_lora_sync_mode, sync_hub_active_loras

                    mode = resolve_lora_sync_mode(args.hostname)
                    await asyncio.to_thread(sync_hub_active_loras, args, mode)
                    await _sync_lora_inventory(state, args)
                    _reconcile_lora_prefetch_wanted(state)
                    if not state.get("_lora_prefetch_wanted"):
                        backoff = 2.0
                        continue
                except Exception as ex:
                    log.warning("Bulk LoRA sync during prefetch failed: %s", ex)

            timeout = 120.0 if wanted else 30.0
            mtype = await api_request_work_download(http, args, state, timeout=timeout)
            if mtype == "download_lora":
                backoff = 2.0
                await _sync_lora_inventory(state, args)
                _reconcile_lora_prefetch_wanted(state)
                continue
            if mtype == "assign_job":
                log.error("request-work returned assign_job during LoRA prefetch — ignored")
            if wanted:
                backoff = min(max(backoff, 3.0), 15.0)
            else:
                backoff = min(backoff * 1.25, 120.0)
            await asyncio.sleep(backoff)
        finally:
            state["_lora_prefetch_downloading"] = False


class _EnvelopeGuard:
    """Release invisible SQS lease on any exit unless explicitly settled (ack/defer/release)."""

    __slots__ = ("_worker", "_envelope", "_job_id", "_settled")

    def __init__(self, worker: ForgeQueueWorker, envelope: JobEnvelope, job_id: str):
        self._worker = worker
        self._envelope = envelope
        self._job_id = job_id
        self._settled = False

    def settle(self, reason: str = "") -> None:
        self._settled = True
        if reason:
            log.debug("SQS lease settled for %s (%s)", self._job_id[:8], reason)

    def release_unsettled(self, reason: str) -> None:
        if self._settled:
            return
        _release_envelope(self._worker, self._envelope, reason=reason)
        self._settled = True

    def __enter__(self) -> _EnvelopeGuard:
        return self

    def __exit__(self, exc_type, exc, _tb) -> bool:
        if not self._settled:
            detail = f"{exc_type.__name__}: {exc}" if exc_type else "unsettled exit"
            self.release_unsettled(detail)
        return False


def _release_envelope(
    worker: ForgeQueueWorker,
    envelope: JobEnvelope,
    *,
    reason: str = "",
    guard: _EnvelopeGuard | None = None,
) -> None:
    """Return message to queue immediately — do not hold a long invisible lease."""
    worker.release_message(envelope, reason=reason or "released")
    if guard:
        guard.settle("released")


def _defer_envelope(
    worker: ForgeQueueWorker,
    envelope: JobEnvelope,
    *,
    seconds: int = 60,
    reason: str = "",
    guard: _EnvelopeGuard | None = None,
) -> None:
    """Short defer for retryable prefetch (LoRA) — never use for wrong-capability jobs."""
    if not envelope.receipt_handle or not envelope.queue_url:
        if guard:
            guard.settle("defer-no-handle")
        return
    timeout = max(0, min(int(seconds), 120))
    worker.extend_visibility(envelope, timeout)
    log.info(
        "Deferred job %s visibility=%ds (%s)",
        envelope.job_id[:8],
        timeout,
        reason or "retry",
    )
    if guard:
        guard.settle("deferred")


async def _visibility_heartbeat(
    worker: ForgeQueueWorker,
    envelope: JobEnvelope,
    stop: asyncio.Event,
) -> None:
    """Keep processing lease alive while Comfy runs; stop when job completes."""
    interval = max(30, int(os.environ.get("FORGE_QUEUE_HEARTBEAT_SECS", "60")))
    lease = int(os.environ.get("FORGE_QUEUE_VISIBILITY", "300"))
    while not stop.is_set():
        try:
            await asyncio.wait_for(stop.wait(), timeout=interval)
            break
        except asyncio.TimeoutError:
            pass
        if stop.is_set():
            break
        try:
            await asyncio.to_thread(worker.extend_visibility, envelope, lease)
        except Exception as ex:
            log.warning("Visibility heartbeat failed for %s: %s", envelope.job_id[:8], ex)


def _complete_envelope(
    worker: ForgeQueueWorker,
    envelope: JobEnvelope,
    args: argparse.Namespace,
    session: SqsGpuSession,
    *,
    status: str = "completed",
    error: str | None = None,
    guard: _EnvelopeGuard | None = None,
) -> None:
    outputs = [
        OutputFile(key=key, url=worker.config.s3_uri(key), content_type=ct)
        for key, ct in session.uploaded_outputs
    ]
    bucket = worker.config.ensure_bucket()
    key = worker.config.result_manifest_key(envelope.job_id)
    manifest: dict[str, Any] = {
        "job_id": envelope.job_id,
        "status": status,
        "outputs": [{"key": o.key, "url": o.url, "content_type": o.content_type} for o in outputs],
        "completed_at": utc_now_iso(),
        "capability": envelope.capability,
        "tier": envelope.tier,
        "tenant_id": envelope.tenant_id,
        "worker_id": args.hostname,
        "hostname": args.hostname,
        "node_uuid": args.node_uuid,
    }
    if error:
        manifest["error"] = error
    if session.last_wd14 is not None:
        manifest["wd14"] = session.last_wd14
    if outputs:
        manifest["output_key"] = outputs[-1].key
    worker.config.s3().put_object(
        Bucket=bucket,
        Key=key,
        Body=json.dumps(manifest, indent=2).encode("utf-8"),
        ContentType="application/json",
    )
    if envelope.reply_queue_url:
        worker.config.sqs().send_message(
            QueueUrl=envelope.reply_queue_url,
            MessageBody=json.dumps(manifest, separators=(",", ":")),
        )
    if envelope.receipt_handle and envelope.queue_url:
        worker.config.sqs().delete_message(
            QueueUrl=envelope.queue_url,
            ReceiptHandle=envelope.receipt_handle,
        )
    if guard:
        guard.settle("completed")


def _maybe_skip_terminal_envelope(worker: ForgeQueueWorker, envelope: JobEnvelope) -> bool:
    """Drop duplicate SQS messages when results/{job}/manifest.json already exists."""
    try:
        bucket = worker.config.ensure_bucket()
        key = worker.config.result_manifest_key(envelope.job_id)
        resp = worker.config.s3().get_object(Bucket=bucket, Key=key)
        body = resp["Body"].read().decode("utf-8", errors="replace")
        data = json.loads(body)
        status = (data.get("status") or "").strip().lower()
        if status == "completed":
            if envelope.receipt_handle and envelope.queue_url:
                worker.config.sqs().delete_message(
                    QueueUrl=envelope.queue_url,
                    ReceiptHandle=envelope.receipt_handle,
                )
            log.info(
                "SQS skip duplicate %s — manifest already %s",
                envelope.job_id[:8],
                status,
            )
            return True
        if status == "failed":
            # Stale failed manifest blocks requeued jobs — delete and retry.
            try:
                worker.config.s3().delete_object(Bucket=bucket, Key=key)
                log.info(
                    "SQS cleared stale failed manifest for %s — retrying",
                    envelope.job_id[:8],
                )
            except Exception as ex:
                log.warning(
                    "SQS failed to clear stale manifest for %s: %s",
                    envelope.job_id[:8], ex,
                )
            return False
    except Exception:
        pass
    return False


async def process_job(
    worker: ForgeQueueWorker,
    envelope: JobEnvelope,
    args: argparse.Namespace,
    state: dict,
    http: aiohttp.ClientSession,
) -> None:
    job_uuid = envelope.job_id
    if await asyncio.to_thread(_maybe_skip_terminal_envelope, worker, envelope):
        return
    hold_since = time.monotonic()
    hold_warn = float(os.environ.get("FORGE_QUEUE_HOLD_WARN_SECS", "120"))
    log.info(
        "SQS holding message %s capability=%s tier=%s tenant=%s",
        job_uuid[:8], envelope.capability, envelope.tier, envelope.tenant_id or "-",
    )

    try:
        with _EnvelopeGuard(worker, envelope, job_uuid) as guard:
            try:
                raw = await asyncio.to_thread(worker.load_payload, envelope)
            except Exception as ex:
                session = SqsGpuSession(args, worker.config, envelope)
                _complete_envelope(
                    worker, envelope, args, session,
                    status="failed", error=str(ex), guard=guard,
                )
                return

            assign = _extract_assign_payload(raw)
            if not isinstance(assign, dict) or assign.get("type") != "assign_job":
                session = SqsGpuSession(args, worker.config, envelope)
                _complete_envelope(
                    worker, envelope, args, session,
                    status="failed", error="SQS payload missing runnable assign_job",
                    guard=guard,
                )
                return

            assign.setdefault("job_uuid", job_uuid)

            if not worker_can_run_assign(
                state,
                assign,
                hostname=args.hostname,
                capability=envelope.capability,
            ):
                model_name = (assign.get("model") or "?").strip()
                await asyncio.to_thread(
                    _release_envelope,
                    worker,
                    envelope,
                    reason=f"model {model_name} not on worker",
                    guard=guard,
                )
                return

            loras_ok, missing_loras = await check_assign_job_loras_sqs(
                args, state, assign,
            )
            if not loras_ok:
                missing_names = ", ".join(_normalize_lora_basename(l) for l in missing_loras)
                _enqueue_lora_prefetch(state, missing_loras)
                defer_counts: dict[str, int] = state.setdefault("_sqs_lora_defer_counts", {})
                count = defer_counts.get(job_uuid, 0) + 1
                defer_counts[job_uuid] = count
                max_defers = int(os.environ.get("FORGE_QUEUE_MAX_LORA_DEFERS", "15"))
                total_defers = int(state.get("_sqs_total_defers", 0))
                max_total = int(os.environ.get("FORGE_QUEUE_MAX_TOTAL_DEFERS", "30"))
                if count >= max_defers or total_defers >= max_total:
                    defer_counts.pop(job_uuid, None)
                    session = SqsGpuSession(args, worker.config, envelope)
                    cap_reason = (
                        f"worker defer cap ({max_total}) exceeded"
                        if total_defers >= max_total
                        else f"missing LoRAs after {count} attempts: {missing_names}"
                    )
                    _complete_envelope(
                        worker, envelope, args, session,
                        status="failed", error=cap_reason, guard=guard,
                    )
                    return
                state["_sqs_total_defers"] = total_defers + 1
                defer_secs = int(os.environ.get("FORGE_QUEUE_LORA_DEFER_SECS", "60"))
                log.info(
                    "Deferred job %s visibility=%ds (missing LoRAs: %s) — "
                    "background download, continuing other work",
                    job_uuid[:8], defer_secs, missing_names,
                )
                await asyncio.to_thread(
                    _defer_envelope,
                    worker,
                    envelope,
                    seconds=defer_secs,
                    reason=f"missing LoRAs: {missing_names}",
                    guard=guard,
                )
                return

            state.get("_sqs_lora_defer_counts", {}).pop(job_uuid, None)
            await asyncio.to_thread(
                worker.extend_visibility,
                envelope,
                int(os.environ.get("FORGE_QUEUE_VISIBILITY", "300")),
            )

            session = SqsGpuSession(args, worker.config, envelope)
            session.uploaded_outputs.clear()
            session.last_wd14 = None
            session.failed_reason = None
            stop_heartbeat = asyncio.Event()
            heartbeat = asyncio.create_task(_visibility_heartbeat(worker, envelope, stop_heartbeat))
            try:
                await accept_assign_job_sqs(session, assign, args, state)
            finally:
                stop_heartbeat.set()
                heartbeat.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await heartbeat

            if session.failed_reason:
                reason = session.failed_reason
                if reason.startswith("Text encoder not on worker"):
                    await asyncio.to_thread(
                        _release_envelope,
                        worker,
                        envelope,
                        reason=reason,
                        guard=guard,
                    )
                else:
                    _complete_envelope(
                        worker, envelope, args, session,
                        status="failed", error=reason, guard=guard,
                    )
            elif session.uploaded_outputs:
                _complete_envelope(worker, envelope, args, session, status="completed", guard=guard)
                log.info("SQS done %s → %s", job_uuid[:8], session.uploaded_outputs[-1][0])
            else:
                _complete_envelope(
                    worker, envelope, args, session,
                    status="failed", error="Worker produced no output", guard=guard,
                )
    finally:
        if state.get("current_job") == job_uuid:
            state["current_job"] = None
        held = time.monotonic() - hold_since
        if held >= hold_warn:
            log.warning(
                "Held invisible SQS message %s for %.0fs (threshold %.0fs)",
                job_uuid[:8], held, hold_warn,
            )

def _make_sqs_workers(
    capabilities: tuple[str, ...],
    args: argparse.Namespace,
    config: ForgeQueueConfig,
    tiers: tuple[str, ...],
) -> list[ForgeQueueWorker]:
    import inspect

    vis = int(os.environ.get("FORGE_QUEUE_VISIBILITY", "300"))
    recv_vis = int(os.environ.get("FORGE_QUEUE_RECEIVE_VISIBILITY", "45"))
    max_idle = float(os.environ.get("FORGE_QUEUE_MAX_IDLE_SECS", "2"))
    idle = float(os.environ.get("FORGE_QUEUE_IDLE_SLEEP", "0"))
    worker_params = inspect.signature(ForgeQueueWorker.__init__).parameters
    workers: list[ForgeQueueWorker] = []
    for cap in capabilities:
        kwargs: dict = {
            "config": config,
            "tiers": tiers,
            "worker_id": args.hostname,
            "visibility_timeout": vis,
            "idle_sleep": idle,
        }
        if "max_idle_secs" in worker_params:
            kwargs["max_idle_secs"] = max_idle
        if "receive_visibility_timeout" in worker_params:
            kwargs["receive_visibility_timeout"] = recv_vis
        workers.append(ForgeQueueWorker(cap, **kwargs))
    return workers


async def _poll_workers(
    workers: list[ForgeQueueWorker],
    state: dict,
    args: argparse.Namespace,
    *,
    max_idle_secs: float = 2.0,
) -> tuple[JobEnvelope | None, ForgeQueueWorker | None]:
    """Poll capability queues for the given tier set (admin→vip→normal, or bulk)."""
    import inspect

    envelope: JobEnvelope | None = None
    active_worker: ForgeQueueWorker | None = None
    pollable = [
        w for w in workers
        if worker_can_poll_capability(state, w.capability, hostname=args.hostname)
    ]
    poll_params = inspect.signature(ForgeQueueWorker.poll).parameters
    supports_max_idle = "max_idle_secs" in poll_params
    for idx, w in enumerate(pollable):
        long_wait = max_idle_secs if idx == len(pollable) - 1 else 0.0
        if supports_max_idle:
            envelope = await asyncio.to_thread(w.poll, max_idle_secs=long_wait)
        else:
            # Stale SDK: long-poll every tier — only wait on last capability poll.
            envelope = await asyncio.to_thread(
                w.poll,
                tier_wait_seconds=int(long_wait) if long_wait > 0 else 0,
            )
        if envelope is not None:
            active_worker = w
            break
    return envelope, active_worker


async def sqs_consumer_loop(
    capabilities: tuple[str, ...],
    args: argparse.Namespace,
    state: dict,
    http: aiohttp.ClientSession,
    config: ForgeQueueConfig,
) -> None:
    priority_workers = _make_sqs_workers(capabilities, args, config, FORGE_PRIORITY_TIERS)
    bulk_workers = _make_sqs_workers(capabilities, args, config, FORGE_BULK_TIERS)
    log.info(
        "SQS consumer capabilities=%s worker_id=%s tiers=%s+%s (bulk when idle)",
        capabilities,
        args.hostname,
        "+".join(FORGE_PRIORITY_TIERS),
        "+".join(FORGE_BULK_TIERS),
    )

    max_idle = float(os.environ.get("FORGE_QUEUE_MAX_IDLE_SECS", "2"))
    extra_idle = float(os.environ.get("FORGE_QUEUE_IDLE_SLEEP", "0"))
    retry_sleep = max_idle
    while True:
        if agent.agent_fleet_busy(state):
            await asyncio.sleep(0.1)
            continue

        envelope: JobEnvelope | None = None
        active_worker: ForgeQueueWorker | None = None
        try:
            envelope, active_worker = await _poll_workers(priority_workers, state, args, max_idle_secs=max_idle)
            # Bulk only after all user-facing tiers are empty and node is still idle.
            if envelope is None and not agent.agent_fleet_busy(state):
                envelope, active_worker = await _poll_workers(bulk_workers, state, args, max_idle_secs=0)
                if envelope is not None:
                    log.debug(
                        "Claimed bulk-tier job %s (node idle, no admin/vip/normal work)",
                        envelope.job_id[:8],
                    )
        except Exception as ex:
            if _retryable_aws_error(ex):
                log.warning(
                    "Forge queue not ready (%s) — sleeping %.0fs and retrying",
                    ex,
                    retry_sleep,
                )
                await asyncio.sleep(retry_sleep)
                continue
            raise

        if envelope is None or active_worker is None:
            if extra_idle > 0:
                await asyncio.sleep(min(extra_idle, max_idle))
            continue

        try:
            await process_job(active_worker, envelope, args, state, http)
        except Exception as ex:
            log.exception(
                "process_job crashed for %s — releasing invisible lease",
                envelope.job_id[:8],
            )
            await asyncio.to_thread(
                _release_envelope, active_worker, envelope, reason=str(ex),
            )


async def api_check_in_once(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    agent_state: dict[str, Any],
) -> bool:
    url = f"{resolve_http_base()}/api/agent/check-in"
    headers = {
        "Authorization": f"Bearer {args.secret}",
        "Content-Type": "application/json",
    }
    if not hasattr(args, "prefix"):
        args.prefix = ""
    if not getattr(args, "server", None):
        args.server = os.environ.get("LOBO_SERVER", "wss://www.loboforge.com")
    payload = build_check_in_payload(args, agent_state)
    payload["transport"] = "sqs"
    payload["forge_queue_capability"] = resolve_capability(args.hostname)
    payload["forge_queue_capabilities"] = list(
        agent_state.get("forge_queue_capabilities") or resolve_capabilities(args.hostname)
    )
    try:
        async with http.post(url, json=payload, headers=headers) as resp:
            if resp.status != 200:
                return False
            data = await resp.json()
            ack = data.get("acknowledged_models") or []
            log.info("API check-in OK — %d models acknowledged", len(ack))
            return True
    except Exception as ex:
        log.warning("API check-in error: %s", ex)
        return False


async def api_check_in_loop(
    http: aiohttp.ClientSession,
    args: argparse.Namespace,
    agent_state: dict[str, Any],
) -> None:
    await api_check_in_once(http, args, agent_state)
    while True:
        await asyncio.sleep(CHECK_IN_INTERVAL)
        await api_check_in_once(http, args, agent_state)


async def run_sqs_agent(args: argparse.Namespace) -> None:
    _require_forge_queue_sdk()
    config = ForgeQueueConfig.from_env()
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

    http_timeout = aiohttp.ClientTimeout(total=45)
    prov_task = await start_sqs_background_provision(args, agent_state) if start_sqs_background_provision else None
    async with aiohttp.ClientSession(timeout=http_timeout) as http:
        tasks = [
            sqs_consumer_loop(capabilities, args, agent_state, http, config),
            sqs_lora_prefetch_loop(http, args, agent_state),
            api_check_in_loop(http, args, agent_state),
            agent.comfy_watchdog(args, agent_state, NoopSession()),
        ]
        if prov_task is not None:
            tasks.append(prov_task)
        await asyncio.gather(*tasks)


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="LoboForge GPU Agent (forge-queue SQS)")
    p.add_argument("--secret", required=True)
    p.add_argument("--node-uuid", default=None)
    p.add_argument("--hostname", default=os.uname().nodename)
    p.add_argument("--capability", default=None)
    p.add_argument("--comfyui-http", default=agent.DEFAULT_COMFYUI_HTTP)
    p.add_argument("--comfyui-ws", default=agent.DEFAULT_COMFYUI_WS)
    p.add_argument("--debug", action="store_true")
    return p


def main() -> None:
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
        datefmt="%H:%M:%S",
    )
    _require_forge_queue_sdk()
    args = build_parser().parse_args()
    if args.debug:
        logging.getLogger().setLevel(logging.DEBUG)
    if args.capability:
        os.environ["FORGE_QUEUE_CAPABILITY"] = args.capability
    if not hasattr(args, "prefix"):
        args.prefix = ""

    uuid_file = Path("~/.loboforge_node_uuid").expanduser()
    if args.node_uuid:
        uuid_file.write_text(args.node_uuid)
    elif uuid_file.exists():
        args.node_uuid = uuid_file.read_text().strip()
    else:
        args.node_uuid = str(uuid.uuid4())
        uuid_file.write_text(args.node_uuid)

    caps = resolve_capabilities(args.hostname)
    log.info(
        "GPU SQS agent starting uuid=%s host=%s capabilities=%s region=%s bucket=%s",
        args.node_uuid,
        args.hostname,
        caps,
        os.environ.get("FORGE_QUEUE_REGION", os.environ.get("AWS_REGION", "us-east-2")),
        os.environ.get("FORGE_QUEUE_BUCKET", "(ssm)"),
    )
    asyncio.run(run_sqs_agent(args))


if __name__ == "__main__":
    main()
