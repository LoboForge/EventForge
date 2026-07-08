#!/usr/bin/env python3
"""JoyCaption EventForge worker — rolling download buffer + continuous GPU captioning.

EventForge returns one job per POST /v1/jobs/claim; this worker keeps claiming until
~BUFFER_DEPTH images are downloaded ahead of the GPU (same pattern as joycaption_worker.py).

Env:
  JOYCAPTION_BUFFER_DEPTH       images ready ahead of GPU (default 20)
  JOYCAPTION_BATCH_SIZE         target claim burst size (default 20)
  JOYCAPTION_MAX_PIPELINE       max claimed-not-done per worker (default 60)
  JOYCAPTION_CLAIM_LOW_WATER    claim more when pipeline below this (default 20)
  JOYCAPTION_DOWNLOAD_CONCURRENCY  parallel downloads (default 8)
  JOYCAPTION_UPLOAD_CONCURRENCY    parallel EventForge complete/upload (default 8)
  JOYCAPTION_GPU_BATCH_SIZE        images per GPU generate() call (default 6)
  JOYCAPTION_GPU_BATCH_WAIT_SEC    wait up to N sec to fill a GPU batch (default 0.12)
"""
from __future__ import annotations

import asyncio
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any

try:
    import aiohttp
except ImportError:
    print("pip install aiohttp", file=sys.stderr)
    raise

EF_URL = (os.environ.get("EVENT_FORGE_URL") or "https://eventforge.loboforge.com").rstrip("/")
WORKER_KEY = os.environ.get("EVENT_FORGE_WORKER_KEY", "wrath-worker-key")
PY = os.environ.get("JOYCAPTION_PYTHON", "/workspace/joycaption/venv/bin/python3")
SERVER = os.environ.get("JOYCAPTION_SERVER_PY", "/workspace/joycaption/joycaption_server.py")
PROMPT = os.environ.get("JOYCAPTION_PROMPT", "/workspace/joycaption/joycaption_prompt.json")
PREPEND = os.environ.get("JOYCAPTION_PREPEND", "")
WORKER_ID = os.environ.get("JOYCAPTION_WORKER_ID") or f"vast-{socket.gethostname()}"
CLAIM_IDLE_SEC = float(os.environ.get("EVENT_FORGE_CLAIM_IDLE", "0.1"))
CHECK_IN_SEC = float(os.environ.get("EVENT_FORGE_CHECK_IN_SEC", "15"))
CAPTION_TIMEOUT_SEC = int(os.environ.get("JOYCAPTION_CAPTION_TIMEOUT_SEC", "600"))
BATCH_SIZE = int(os.environ.get("JOYCAPTION_BATCH_SIZE", "40"))
BUFFER_DEPTH = int(os.environ.get("JOYCAPTION_BUFFER_DEPTH", "40"))
MAX_PIPELINE = int(os.environ.get("JOYCAPTION_MAX_PIPELINE", "120"))
CLAIM_LOW_WATER = int(os.environ.get("JOYCAPTION_CLAIM_LOW_WATER", "40"))
DOWNLOAD_CONCURRENCY = int(os.environ.get("JOYCAPTION_DOWNLOAD_CONCURRENCY", "12"))
UPLOAD_CONCURRENCY = int(os.environ.get("JOYCAPTION_UPLOAD_CONCURRENCY", "12"))
GPU_BATCH_SIZE = max(1, int(os.environ.get("JOYCAPTION_GPU_BATCH_SIZE", "6")))
GPU_BATCH_WAIT_SEC = float(os.environ.get("JOYCAPTION_GPU_BATCH_WAIT_SEC", "0.12"))
DOWNLOAD_RETRIES = int(os.environ.get("JOYCAPTION_DOWNLOAD_RETRIES", "5"))
DOWNLOAD_RETRY_SEC = float(os.environ.get("JOYCAPTION_DOWNLOAD_RETRY_SEC", "0.75"))
MIN_IMAGE_BYTES = int(os.environ.get("JOYCAPTION_MIN_IMAGE_BYTES", "512"))

EF_HEADERS = {
    "Authorization": f"Bearer {WORKER_KEY}",
    "Content-Type": "application/json",
    "Accept": "application/json",
}


@dataclass
class JobWork:
    job_id: str
    external_id: str
    image_url: str
    prepend: str
    prompt_key: str = "default"


@dataclass
class UploadWork:
    job_id: str
    external_id: str
    caption: str
    path: Path


def server_quant_flags() -> list[str]:
    try:
        import torch

        if torch.cuda.is_available():
            vram_gb = torch.cuda.get_device_properties(0).total_memory / (1024**3)
            name = torch.cuda.get_device_name(0)
            if vram_gb > 16:
                print(f"[ef-worker] GPU {name} ({vram_gb:.1f}GB) — bf16", flush=True)
                return []
            print(f"[ef-worker] GPU {name} ({vram_gb:.1f}GB) — 8-bit", flush=True)
            return ["--load-in-8bit"]
    except Exception as ex:
        print(f"[ef-worker] GPU probe failed ({ex}) — 8-bit", flush=True)
    return ["--load-in-8bit"]


@dataclass
class Pipeline:
    """Rolling pipeline: claim → job_inbox → download → ready_queue → GPU."""

    job_inbox: asyncio.Queue[JobWork]
    ready_queue: asyncio.Queue[tuple[JobWork, Path]]
    lock: asyncio.Lock
    outstanding: int  # claimed, not yet completed on EventForge
    downloading: int
    claims_in_flight: int
    no_work_until: float = 0.0

    @classmethod
    def create(cls) -> Pipeline:
        return cls(
            job_inbox=asyncio.Queue(),
            ready_queue=asyncio.Queue(maxsize=max(BUFFER_DEPTH, GPU_BATCH_SIZE * 8)),
            lock=asyncio.Lock(),
            outstanding=0,
            downloading=0,
            claims_in_flight=0,
        )

    async def inbox_depth(self) -> int:
        async with self.lock:
            return self.job_inbox.qsize() + self.downloading + self.ready_queue.qsize()

    async def add_claim_batch(self, jobs: list[JobWork]) -> None:
        if not jobs:
            return
        async with self.lock:
            self.outstanding += len(jobs)
            out = self.outstanding
        for work in jobs:
            await self.job_inbox.put(work)
        print(f"[ef-worker] claimed batch: {len(jobs)} jobs (outstanding {out})", flush=True)


class JoyCaptionServer:
    def __init__(self) -> None:
        self.proc: subprocess.Popen | None = None

    def start(self) -> None:
        if self.proc is not None and self.proc.poll() is None:
            return
        cmd = [PY, SERVER, "--prompt-file", PROMPT, *server_quant_flags()]
        print(f"[ef-worker] starting JoyCaption server: {' '.join(cmd)}", flush=True)
        self.proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=sys.stderr,
            text=True,
            bufsize=1,
        )
        assert self.proc.stdout is not None
        line = self.proc.stdout.readline()
        if not line:
            raise RuntimeError("JoyCaption server exited before ready")
        data = json.loads(line)
        if not data.get("ready"):
            raise RuntimeError(f"JoyCaption server failed: {line.strip()}")
        print("[ef-worker] JoyCaption model ready", flush=True)

    def caption(self, image_path: Path, prepend: str, prompt_key: str = "default") -> str:
        self.start()
        assert self.proc and self.proc.stdin and self.proc.stdout
        req = {
            "cmd": "caption",
            "path": str(image_path),
            "prepend": prepend,
            "append": "",
            "prompt_key": prompt_key or "default",
        }
        self.proc.stdin.write(json.dumps(req) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            self.proc = None
            raise RuntimeError("JoyCaption server died during caption")
        data = json.loads(line)
        if "error" in data:
            raise RuntimeError(data["error"])
        return str(data.get("caption", "")).strip()

    def caption_batch(
        self, items: list[tuple[str, Path, str, str]]
    ) -> list[tuple[str, str | None, str | None]]:
        """Returns list of (job_id, caption|None, error|None)."""
        self.start()
        assert self.proc and self.proc.stdin and self.proc.stdout
        payload = {
            "cmd": "caption_batch",
            "items": [
                {
                    "jobId": job_id,
                    "path": str(path),
                    "prepend": prepend,
                    "append": "",
                    "prompt_key": prompt_key or "default",
                }
                for job_id, path, prepend, prompt_key in items
            ],
        }
        self.proc.stdin.write(json.dumps(payload) + "\n")
        self.proc.stdin.flush()
        line = self.proc.stdout.readline()
        if not line:
            self.proc = None
            raise RuntimeError("JoyCaption server died during caption_batch")
        data = json.loads(line)
        if "error" in data and "results" not in data:
            raise RuntimeError(data["error"])
        out: list[tuple[str, str | None, str | None]] = []
        for row in data.get("results") or []:
            jid = str(row.get("jobId") or "")
            if "caption" in row:
                out.append((jid, str(row.get("caption") or "").strip(), None))
            else:
                out.append((jid, None, str(row.get("error") or "caption failed")))
        return out

    def stop(self) -> None:
        if self.proc and self.proc.poll() is None:
            try:
                if self.proc.stdin:
                    self.proc.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
                    self.proc.stdin.flush()
            except Exception:
                pass
            try:
                self.proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proc.kill()
        self.proc = None


server = JoyCaptionServer()


def pipeline_busy(state: dict[str, Any], pipe: Pipeline) -> bool:
    """Stay 'busy' while any stage of the pipeline has work (ops must not show idle at 38k queued)."""
    if state.get("current_job") or state.get("gpu_batch"):
        return True
    if pipe.outstanding > 0 or pipe.claims_in_flight > 0 or pipe.downloading > 0:
        return True
    try:
        if pipe.job_inbox.qsize() > 0 or pipe.ready_queue.qsize() > 0:
            return True
    except Exception:
        pass
    return False


def build_check_in_payload(state: dict[str, Any], pipe: Pipeline) -> dict[str, Any]:
    payload: dict[str, Any] = {
        "node_uuid": WORKER_ID,
        "hostname": WORKER_ID,
        "transport": "eventforge",
        "forge_queue_capabilities": ["caption"],
        "claim_ready_capabilities": ["caption"],
        "busy": pipeline_busy(state, pipe),
        "comfy_ok": True,
        "queue_access_ok": True,
    }
    job_id = state.get("current_job")
    if job_id:
        payload["current_job_uuid"] = job_id
    try:
        import torch

        if torch.cuda.is_available():
            props = torch.cuda.get_device_properties(0)
            payload["gpu_name"] = props.name
            payload["vram_total"] = int(props.total_memory / (1024 * 1024))
            payload["vram_free"] = int(torch.cuda.mem_get_info()[0] / (1024 * 1024))
    except Exception:
        pass
    try:
        usage = shutil.disk_usage("/workspace")
        payload["disk_free_mb"] = int(usage.free / (1024 * 1024))
    except Exception:
        pass
    return payload


def parse_assign(payload: dict, job_id: str) -> dict | None:
    if payload.get("type") == "assign_job":
        out = dict(payload)
        out.setdefault("job_uuid", job_id)
        return out
    inner = payload.get("payload")
    if isinstance(inner, dict) and inner.get("type") == "assign_job":
        out = dict(inner)
        out.setdefault("job_uuid", job_id)
        return out
    if payload.get("type") == "caption":
        out = {
            "type": "assign_job",
            "job_uuid": job_id,
            "model": "joycaption",
            "caption": True,
            "external_id": payload.get("external_id") or "",
            "prepend": PREPEND,
            "prompt_key": payload.get("prompt_key") or "default",
            "ref_images": payload.get("ref_images") or [],
        }
        upload_uuid = payload.get("upload_uuid")
        if upload_uuid and not out["ref_images"]:
            out["ref_images"] = [{"url": f"{EF_URL}/v1/jobs/{job_id}/input/ref.jpg"}]
        return out
    return None


def job_work_from_claim(job: dict) -> JobWork | None:
    job_id = str(job.get("job_id") or "")
    payload = job.get("payload") or {}
    assign = parse_assign(payload if isinstance(payload, dict) else {}, job_id)
    if not assign:
        return None

    ref_images = assign.get("ref_images") or []
    image_url = None
    if ref_images:
        first = ref_images[0]
        if isinstance(first, dict):
            image_url = first.get("url") or first.get("fallback_url")
        else:
            image_url = first
    if not image_url:
        image_url = f"{EF_URL}/v1/jobs/{job_id}/input/ref.jpg"

    prompt_key = str(assign.get("prompt_key") or "default").strip() or "default"
    return JobWork(
        job_id=job_id,
        external_id=str(assign.get("external_id") or ""),
        image_url=image_url,
        prepend=str(assign.get("prepend") or PREPEND),
        prompt_key=prompt_key,
    )


def _validate_image_bytes(data: bytes) -> None:
    if len(data) < MIN_IMAGE_BYTES:
        raise ValueError(f"download too small ({len(data)} bytes)")
    if data[:15].lstrip().startswith(b"<!") or data[:1] == b"<":
        raise ValueError("download looks like HTML, not an image")
    try:
        from PIL import Image
        import io

        with Image.open(io.BytesIO(data)) as im:
            im.verify()
    except Exception as ex:
        raise ValueError(f"not a valid image: {ex}") from ex


async def download_image(session: aiohttp.ClientSession, url: str, headers: dict) -> Path:
    """Retry — workers often claim before orchestrator upload finishes."""
    last_err: Exception | None = None
    suffix = ".jpg"
    base = url.split("?")[0].split("/")[-1]
    if "." in base:
        suffix = "." + base.rsplit(".", 1)[-1]

    for attempt in range(DOWNLOAD_RETRIES):
        try:
            async with session.get(url, headers=headers) as resp:
                if resp.status in (404, 409, 425, 503):
                    raise aiohttp.ClientResponseError(
                        resp.request_info, resp.history, status=resp.status, message=await resp.text()
                    )
                resp.raise_for_status()
                data = await resp.read()
            _validate_image_bytes(data)
            fd, path = tempfile.mkstemp(suffix=suffix, prefix="joycap_")
            os.close(fd)
            p = Path(path)
            p.write_bytes(data)
            return p
        except Exception as ex:
            last_err = ex
            if attempt + 1 < DOWNLOAD_RETRIES:
                delay = DOWNLOAD_RETRY_SEC * (attempt + 1)
                await asyncio.sleep(delay)
    raise RuntimeError(f"download failed after {DOWNLOAD_RETRIES} tries: {last_err}")


async def ef_check_in(session: aiohttp.ClientSession, state: dict[str, Any], pipe: Pipeline) -> bool:
    payload = build_check_in_payload(state, pipe)
    try:
        async with session.post(f"{EF_URL}/v1/workers/check-in", json=payload, headers=EF_HEADERS) as resp:
            if resp.status != 200:
                text = await resp.text()
                print(f"[ef-worker] check-in HTTP {resp.status}: {text[:200]}", flush=True)
                return False
        return True
    except Exception as ex:
        print(f"[ef-worker] check-in error: {ex}", flush=True)
        return False


async def check_in_loop(session: aiohttp.ClientSession, state: dict[str, Any], pipe: Pipeline) -> None:
    await ef_check_in(session, state, pipe)
    while True:
        await asyncio.sleep(CHECK_IN_SEC)
        await ef_check_in(session, state, pipe)


async def ef_claim(session: aiohttp.ClientSession) -> dict | None:
    body = {"capabilities": ["caption"], "hostname": WORKER_ID}
    async with session.post(f"{EF_URL}/v1/jobs/claim", json=body, headers=EF_HEADERS) as resp:
        if resp.status == 204:
            return None
        if resp.status == 401:
            raise RuntimeError("EventForge worker key rejected")
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"EventForge claim HTTP {resp.status}: {text[:200]}")
        return await resp.json()


async def ef_complete(session: aiohttp.ClientSession, job_id: str, caption: str) -> None:
    data = caption.encode("utf-8")
    out_url = f"{EF_URL}/v1/jobs/{job_id}/output?file=caption.txt"
    async with session.put(
        out_url,
        data=data,
        headers={**EF_HEADERS, "Content-Type": "text/plain; charset=utf-8"},
    ) as resp:
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"output upload HTTP {resp.status}: {text[:200]}")
    async with session.post(
        f"{EF_URL}/v1/jobs/{job_id}/complete", json={"text": caption}, headers=EF_HEADERS
    ) as resp:
        if resp.status >= 400:
            text = await resp.text()
            raise RuntimeError(f"complete HTTP {resp.status}: {text[:200]}")


async def ef_fail(session: aiohttp.ClientSession, job_id: str, error: str) -> None:
    async with session.post(
        f"{EF_URL}/v1/jobs/{job_id}/fail", json={"error": error}, headers=EF_HEADERS
    ) as resp:
        if resp.status >= 400:
            print(f"[ef-worker] fail HTTP {resp.status}: {await resp.text()}", flush=True)


async def claim_one(session: aiohttp.ClientSession, pipe: Pipeline) -> JobWork | None:
    async with pipe.lock:
        pipe.claims_in_flight += 1
    try:
        job = await ef_claim(session)
    finally:
        async with pipe.lock:
            pipe.claims_in_flight = max(0, pipe.claims_in_flight - 1)
    if job is None:
        return None
    work = job_work_from_claim(job)
    if work is None:
        await ef_fail(session, str(job.get("job_id") or ""), "payload missing assign_job / caption")
        return None
    return work


async def claim_loop(session: aiohttp.ClientSession, pipe: Pipeline, stop: asyncio.Event) -> None:
    """Claim up to BATCH_SIZE (20) jobs in parallel whenever pipeline drops below CLAIM_LOW_WATER."""
    claim_errors = 0
    while not stop.is_set():
        if time.time() < pipe.no_work_until:
            await asyncio.sleep(0.25)
            continue

        depth = await pipe.inbox_depth()
        async with pipe.lock:
            outstanding = pipe.outstanding
            claims = pipe.claims_in_flight
        if depth >= CLAIM_LOW_WATER or outstanding + claims * BATCH_SIZE >= MAX_PIPELINE:
            await asyncio.sleep(0.15)
            continue

        size = min(BATCH_SIZE, MAX_PIPELINE - outstanding - claims * BATCH_SIZE)
        if size <= 0:
            await asyncio.sleep(0.25)
            continue

        try:
            results = await asyncio.gather(
                *[claim_one(session, pipe) for _ in range(size)],
                return_exceptions=True,
            )
        except Exception as ex:
            claim_errors += 1
            print(f"[ef-worker] claim burst error: {ex}", flush=True)
            await asyncio.sleep(min(CLAIM_IDLE_SEC * claim_errors, 30))
            continue

        batch: list[JobWork] = []
        saw_empty = False
        for r in results:
            if isinstance(r, Exception):
                claim_errors += 1
                print(f"[ef-worker] claim error: {r}", flush=True)
                continue
            claim_errors = 0
            if r is None:
                saw_empty = True
            else:
                batch.append(r)

        if batch:
            await pipe.add_claim_batch(batch)
        if saw_empty and not batch:
            pipe.no_work_until = time.time() + CLAIM_IDLE_SEC
        elif saw_empty:
            await asyncio.sleep(0.1)


async def download_one(
    session: aiohttp.ClientSession,
    pipe: Pipeline,
    work: JobWork,
    headers: dict,
    dl_sem: asyncio.Semaphore,
) -> None:
    async with dl_sem:
        async with pipe.lock:
            pipe.downloading += 1
        try:
            path = await download_image(session, work.image_url, headers)
            await pipe.ready_queue.put((work, path))
        except Exception as ex:
            print(f"[ef-worker] download failed {work.job_id[:8]}: {ex}", flush=True)
            await ef_fail(session, work.job_id, str(ex))
            async with pipe.lock:
                pipe.outstanding = max(0, pipe.outstanding - 1)
        finally:
            async with pipe.lock:
                pipe.downloading = max(0, pipe.downloading - 1)


async def download_loop(
    session: aiohttp.ClientSession,
    pipe: Pipeline,
    dl_sem: asyncio.Semaphore,
    stop: asyncio.Event,
) -> None:
    """Fill ready_queue ahead of GPU — up to DOWNLOAD_CONCURRENCY parallel fetches."""
    headers = {"Authorization": f"Bearer {WORKER_KEY}"}
    pending: set[asyncio.Task] = set()
    while not stop.is_set():
        while pending and len(pending) >= DOWNLOAD_CONCURRENCY:
            done, pending = await asyncio.wait(pending, return_when=asyncio.FIRST_COMPLETED)
            for t in done:
                t.exception()
        try:
            work = await asyncio.wait_for(pipe.job_inbox.get(), timeout=0.5)
        except asyncio.TimeoutError:
            continue
        pending.add(
            asyncio.create_task(download_one(session, pipe, work, headers, dl_sem))
        )
    if pending:
        await asyncio.gather(*pending, return_exceptions=True)


async def upload_loop(
    session: aiohttp.ClientSession,
    pipe: Pipeline,
    upload_queue: asyncio.Queue[UploadWork | None],
    stop: asyncio.Event,
) -> None:
    sem = asyncio.Semaphore(UPLOAD_CONCURRENCY)
    while not stop.is_set():
        try:
            item = await asyncio.wait_for(upload_queue.get(), timeout=0.5)
        except asyncio.TimeoutError:
            continue
        if item is None:
            break
        async with sem:
            try:
                await ef_complete(session, item.job_id, item.caption)
                print(
                    f"[ef-worker] complete {item.job_id[:8]} — {item.external_id} ({len(item.caption)} chars)",
                    flush=True,
                )
            except Exception as ex:
                print(
                    f"[ef-worker] upload failed {item.job_id[:8]} — {item.external_id}: {ex}",
                    flush=True,
                )
                await ef_fail(session, item.job_id, str(ex))
            finally:
                try:
                    item.path.unlink()
                except OSError:
                    pass
                async with pipe.lock:
                    pipe.outstanding = max(0, pipe.outstanding - 1)
                upload_queue.task_done()


async def gather_gpu_batch(
    ready_queue: asyncio.Queue[tuple[JobWork, Path]],
    stop: asyncio.Event,
) -> list[tuple[JobWork, Path]]:
    """Accumulate up to GPU_BATCH_SIZE images; brief wait to fill the batch."""
    batch: list[tuple[JobWork, Path]] = []
    while not stop.is_set() and len(batch) < GPU_BATCH_SIZE:
        timeout = GPU_BATCH_WAIT_SEC if batch else 0.5
        try:
            item = await asyncio.wait_for(ready_queue.get(), timeout=timeout)
        except asyncio.TimeoutError:
            break
        batch.append(item)
    return batch


def caption_batch_timeout(batch_len: int) -> float:
    return float(CAPTION_TIMEOUT_SEC) + max(0, batch_len - 1) * 45.0


async def caption_loop(
    session: aiohttp.ClientSession,
    pipe: Pipeline,
    state: dict[str, Any],
    upload_queue: asyncio.Queue[UploadWork | None],
    stop: asyncio.Event,
) -> None:
    """Batch GPU inference — up to GPU_BATCH_SIZE images per model.generate() call."""
    while not stop.is_set():
        batch = await gather_gpu_batch(pipe.ready_queue, stop)
        if not batch:
            continue

        state["gpu_batch"] = len(batch)
        state["current_job"] = batch[0][0].job_id if len(batch) == 1 else f"{len(batch)}-way"
        items = [(work.job_id, path, work.prepend, work.prompt_key) for work, path in batch]
        try:
            results = await asyncio.wait_for(
                asyncio.to_thread(server.caption_batch, items),
                timeout=caption_batch_timeout(len(batch)),
            )
            by_id = {job_id: (caption, err) for job_id, caption, err in results}
            for work, path in batch:
                caption, err = by_id.get(work.job_id, (None, "missing batch result"))
                if err or not caption:
                    msg = err or "JoyCaption returned empty caption"
                    print(f"[ef-worker] failed {work.job_id[:8]} — {work.external_id}: {msg}", flush=True)
                    await ef_fail(session, work.job_id, msg)
                    try:
                        path.unlink()
                    except OSError:
                        pass
                    async with pipe.lock:
                        pipe.outstanding = max(0, pipe.outstanding - 1)
                    continue
                await upload_queue.put(
                    UploadWork(job_id=work.job_id, external_id=work.external_id, caption=caption, path=path)
                )
            print(f"[ef-worker] GPU batch done: {len(batch)} images", flush=True)
        except asyncio.TimeoutError:
            server.stop()
            err = f"GPU batch timed out after {caption_batch_timeout(len(batch)):.0f}s"
            print(f"[ef-worker] {err}", flush=True)
            for work, path in batch:
                await ef_fail(session, work.job_id, err)
                try:
                    path.unlink()
                except OSError:
                    pass
                async with pipe.lock:
                    pipe.outstanding = max(0, pipe.outstanding - 1)
        except Exception as ex:
            print(f"[ef-worker] GPU batch error: {ex}", flush=True)
            if "JoyCaption server died" in str(ex):
                server.stop()
            for work, path in batch:
                await ef_fail(session, work.job_id, str(ex))
                try:
                    path.unlink()
                except OSError:
                    pass
                async with pipe.lock:
                    pipe.outstanding = max(0, pipe.outstanding - 1)
        finally:
            state["gpu_batch"] = 0
            state["current_job"] = None


async def run_pipeline(session: aiohttp.ClientSession, state: dict[str, Any]) -> None:
    pipe = Pipeline.create()
    stop = asyncio.Event()
    dl_sem = asyncio.Semaphore(DOWNLOAD_CONCURRENCY)
    upload_queue: asyncio.Queue[UploadWork | None] = asyncio.Queue(maxsize=MAX_PIPELINE)
    tasks = [
        asyncio.create_task(check_in_loop(session, state, pipe)),
        asyncio.create_task(claim_loop(session, pipe, stop)),
        asyncio.create_task(download_loop(session, pipe, dl_sem, stop)),
        asyncio.create_task(caption_loop(session, pipe, state, upload_queue, stop)),
        asyncio.create_task(upload_loop(session, pipe, upload_queue, stop)),
    ]
    try:
        await asyncio.gather(*tasks)
    except asyncio.CancelledError:
        stop.set()
        raise
    finally:
        stop.set()
        for t in tasks:
            t.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)


async def main() -> None:
    print(
        f"[ef-worker] JoyCaption EventForge worker {WORKER_ID} → {EF_URL} "
        f"(buffer={BUFFER_DEPTH}, claim_batch={BATCH_SIZE}, gpu_batch={GPU_BATCH_SIZE}, "
        f"max_pipeline={MAX_PIPELINE}, dl={DOWNLOAD_CONCURRENCY}, upload={UPLOAD_CONCURRENCY})",
        flush=True,
    )
    state: dict[str, Any] = {"current_job": None}
    print("[ef-worker] warming JoyCaption model…", flush=True)
    await asyncio.to_thread(server.start)
    timeout = aiohttp.ClientTimeout(total=None, sock_connect=30, sock_read=600)
    attempt = 0
    while True:
        try:
            async with aiohttp.ClientSession(timeout=timeout) as session:
                await run_pipeline(session, state)
            attempt = 0
        except Exception as ex:
            attempt += 1
            print(f"[ef-worker] pipeline error: {ex}", flush=True)
            server.stop()
            delay = min(CLAIM_IDLE_SEC * (2 ** min(attempt, 4)), 60)
            await asyncio.sleep(delay)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        server.stop()
