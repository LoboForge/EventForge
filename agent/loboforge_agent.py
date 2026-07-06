#!/usr/bin/env python3
"""
LoboForge GPU Agent
===================
Maintains a persistent WebSocket connection to the LoboForge server,
receives generation jobs, submits them to local ComfyUI, streams
progress back, and transfers completed files via chunked binary frames.

Usage:
    python3 loboforge_agent.py --server wss://loboforge.com --secret NODE_SECRET

Requirements:
    pip install websockets aiohttp
"""

import asyncio
import aiohttp
import websockets
import websockets.exceptions
import json
import uuid
import os
import sys
import struct
import hashlib
import argparse
import logging
import subprocess
import time
import shutil
from pathlib import Path
from typing import Optional
from urllib.parse import urlparse, urlunparse, quote, unquote
import urllib.request

try:
    from loboforge_worker.env_loader import ensure_loboforge_env
except ImportError:
    def ensure_loboforge_env(*_args, **_kwargs) -> None:  # type: ignore[misc]
        pass

ensure_loboforge_env()

# ── Optional WD14 auto-tagging ─────────────────────────────────────────────
# Imported lazily so the agent still works on boxes without onnxruntime/Pillow.
# When available, each generated image is tagged before it's sent to the server
# and the result is piggybacked onto the file_begin message.
try:
    import wd14_tagger  # noqa: F401
    WD14_AVAILABLE = True
except Exception as _wd14_e:
    wd14_tagger = None
    WD14_AVAILABLE = False
    _WD14_IMPORT_ERROR = str(_wd14_e)

try:
    import joycaption_runner
    JOYCAPTION_AVAILABLE = joycaption_runner.is_available()
except Exception as _jc_e:
    joycaption_runner = None
    JOYCAPTION_AVAILABLE = False
    _JOYCAPTION_IMPORT_ERROR = str(_jc_e)

# ── Defaults ──────────────────────────────────────────────────────────────────
DEFAULT_SERVER       = "wss://loboforge.com"
DEFAULT_COMFYUI_HTTP = "http://127.0.0.1:8188"
DEFAULT_COMFYUI_WS   = "ws://127.0.0.1:8188"
RECONNECT_MIN        = 2      # seconds — first retry
RECONNECT_MAX        = 5      # seconds — keep low, server restarts frequently
HEARTBEAT_INTERVAL   = 15     # seconds — app-level keepalive (jobs can run 10+ min)
MAX_JOB_WAIT         = 7200   # max seconds of SILENCE between ComfyUI WS messages
                             # Wall-clock caps are enforced separately in max_job_total_seconds().
JOB_ACTIVITY_PING    = 30     # keepalive to prod while Comfy is silent (encode/mux)
CHUNK_SIZE           = 65536  # 64 KB per binary frame

# Protocol pings are safe now that jobs run in background tasks (recv loop stays live).
# Use a long timeout so 5–10 min ComfyUI runs never trip keepalive.
WS_PING_INTERVAL     = 20
WS_PING_TIMEOUT      = 300
COMFY_HEALTH_INTERVAL = 60    # background ComfyUI probe
COMFY_RESTART_COOLDOWN = 120  # min seconds between tmux restarts
COMFY_STARTUP_WAIT    = 180   # max seconds to wait after restart

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S"
)
log = logging.getLogger("agent")


def max_job_total_seconds(model: str | None) -> int:
    """Hard wall clock — healthy Klein edits finish in seconds; this catches broken Comfy/workers."""
    m = (model or "").lower()
    if m.startswith("ltx"):
        return 900  # healthy LTX t2v ~8m + encode/mux
    if m.startswith("wan") or m in ("storyboard",):
        return 600  # healthy Wan i2v ~5m + encode/mux
    if m in ("music", "ace-step"):
        return 240
    return 150


_WAN_GRAPH_NODE_TYPES = frozenset({
    "WanImageToVideo",
    "WanVideoToVideo",
    "WanFirstLastFrameToVideo",
    "Wan22ImageToVideo",
    "Wan22FunCameraImageToVideo",
})


def graph_has_wan_nodes(graph: dict) -> bool:
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        ct = node.get("class_type") or ""
        if ct in _WAN_GRAPH_NODE_TYPES or str(ct).startswith("Wan"):
            return True
    return False


def resolve_job_wall_seconds(
    model: str | None,
    graph: dict | None,
    server_cap: int | None = None,
) -> int:
    """Prefer server-provided cap; else model; else infer from workflow graph."""
    if server_cap and server_cap > 0:
        return int(server_cap)
    wall = max_job_total_seconds(model)
    if wall > 150 or not graph:
        return wall
    if graph_has_ltx_nodes(graph):
        return 900
    if graph_has_wan_nodes(graph):
        return 600
    return wall


def resolve_fleet_mode(args) -> str:
    """Provision mode for server routing — from env, not hostname format."""
    pool = os.environ.get("LOBO_GEN_POOL", "").strip().lower()
    if pool in ("both", "all"):
        return "all"
    if pool == "video":
        return "video"
    if pool == "image":
        return "image"
    for key in ("LOBO_MODE", "MODE"):
        raw = os.environ.get(key, "").strip().lower()
        if raw:
            return raw.split(",")[0].strip()
    label = os.environ.get("LOBO_LABEL", "").strip().lower()
    if "-image" in label or label.endswith("image"):
        return "image"
    if "-video" in label or label.endswith("video"):
        return "video"
    explicit = getattr(args, "provision_mode", None) or ""
    if explicit:
        return str(explicit).strip().lower()
    return "all"


# ── System / GPU info ──────────────────────────────────────────────────────────

def get_gpu_info() -> dict:
    try:
        r = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total,memory.free",
             "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5
        )
        if r.returncode == 0:
            # Multi-GPU boxes return one CSV line per device — use the first GPU only.
            first_line = r.stdout.strip().splitlines()[0]
            parts = [p.strip() for p in first_line.split(",")]
            return {
                "gpu_name":   parts[0] if len(parts) > 0 else "Unknown",
                "vram_total": int(parts[1]) if len(parts) > 1 else 0,
                "vram_free":  int(parts[2]) if len(parts) > 2 else 0,
            }
    except Exception:
        pass
    return {"gpu_name": "Unknown", "vram_total": 0, "vram_free": 0}


def get_vram_free() -> int:
    info = get_gpu_info()
    return info["vram_free"]


def get_disk_free_mb(path: str = "/") -> int:
    try:
        return int(shutil.disk_usage(path).free // (1024 * 1024))
    except Exception:
        return -1


_LOAD_IO_CLASSES = frozenset({
    "LoadImage", "LoadImageMask", "LoadVideo", "VHS_LoadVideo", "VHS_LoadAudio",
    "LoadAudio", "ImageLoad", "LoadImageSequence",
})
_SAVE_IO_CLASSES = frozenset({"SaveImage", "SaveVideo", "VHS_SaveVideo", "VHS_SaveImages"})


def _comfy_queue_io_names(comfyui_url: str) -> tuple[set[str], set[str], bool]:
    """Return (protected input basenames, output filename prefixes, queue_running)."""
    protected: set[str] = set()
    prefixes: set[str] = set()
    has_running = False
    headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
    try:
        req = urllib.request.Request(f"{comfyui_url.rstrip('/')}/queue", headers=headers)
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode())
    except Exception:
        return protected, prefixes, False

    for key in ("queue_running", "queue_pending"):
        entries = data.get(key) or []
        if key == "queue_running" and entries:
            has_running = True
        for entry in entries:
            prompt = entry[2] if isinstance(entry, (list, tuple)) and len(entry) > 2 else None
            if not isinstance(prompt, dict):
                continue
            for node in prompt.values():
                if not isinstance(node, dict):
                    continue
                ctype = node.get("class_type") or ""
                inputs = node.get("inputs") or {}
                if not isinstance(inputs, dict):
                    continue
                if ctype in _LOAD_IO_CLASSES or "load" in ctype.lower():
                    for v in inputs.values():
                        if isinstance(v, str) and v and not v.startswith("/") and "/" not in v:
                            protected.add(os.path.basename(v))
                if ctype in _SAVE_IO_CLASSES:
                    prefix = inputs.get("filename_prefix") or inputs.get("prefix")
                    if isinstance(prefix, str) and prefix.strip():
                        prefixes.add(prefix.strip())
    return protected, prefixes, has_running


def _should_keep_comfy_io_path(
    path: Path,
    sub: str,
    *,
    protected_names: set[str],
    protected_prefixes: set[str],
    has_running: bool,
    recent_cutoff: float,
) -> bool:
    if path.name in ("example.png",):
        return True
    if path.name in protected_names:
        return True
    if sub == "output" and protected_prefixes:
        if any(path.name.startswith(pref) for pref in protected_prefixes):
            return True
    if sub == "output" and has_running:
        try:
            if path.stat().st_mtime >= recent_cutoff:
                return True
        except OSError:
            return True
    return False


def maybe_free_comfy_disk(args, min_free_mb: int = 2048) -> None:
    """Drop Comfy input/output/temp when root disk is tight; keep active queue files."""
    free = get_disk_free_mb()
    if free < 0 or free >= min_free_mb:
        return
    comfy_dir = _find_comfy_dir(args)
    if not comfy_dir:
        return
    comfy_url = getattr(args, "comfyui_http", None) or "http://127.0.0.1:18188"
    protected, prefixes, has_running = _comfy_queue_io_names(comfy_url)
    recent_cutoff = time.time() - (180 * 60)
    freed = 0
    for sub in ("output", "temp", "input"):
        target = comfy_dir / sub
        if not target.is_dir():
            continue
        for p in target.iterdir():
            if _should_keep_comfy_io_path(
                p,
                sub,
                protected_names=protected,
                protected_prefixes=prefixes,
                has_running=has_running and sub == "output",
                recent_cutoff=recent_cutoff,
            ):
                continue
            try:
                if p.is_file():
                    freed += p.stat().st_size
                    p.unlink()
                elif p.is_dir():
                    freed += sum(f.stat().st_size for f in p.rglob("*") if f.is_file())
                    shutil.rmtree(p, ignore_errors=True)
            except OSError:
                pass
    if freed:
        log.warning(
            "Low disk (%dMB free) — cleared %dMB from Comfy %s (kept %d queue inputs, running=%s)",
            free,
            freed // (1024 * 1024),
            comfy_dir,
            len(protected),
            has_running,
        )


def is_p100_gpu(gpu_name: str | None = None) -> bool:
    name = (gpu_name or get_gpu_info().get("gpu_name") or "").lower()
    return "p100" in name


def _comfy_python() -> str:
    venv_py = Path("/venv/main/bin/python")
    if venv_py.is_file():
        return str(venv_py)
    return sys.executable


def pytorch_cuda_kernel_works(pybin: str | None = None, *, include_torchaudio: bool = False) -> bool:
    py = pybin or _comfy_python()
    probe = (
        "import torch; import torchaudio; "
        "assert torch.cuda.is_available(); "
        "x=torch.zeros(1,device='cuda'); "
        "(x+1).item(); torch.cuda.synchronize()"
        if include_torchaudio else
        "import torch; "
        "assert torch.cuda.is_available(); "
        "x=torch.zeros(1,device='cuda'); "
        "(x+1).item(); "
        "torch.cuda.synchronize()"
    )
    try:
        r = subprocess.run(
            [py, "-c", probe],
            capture_output=True, text=True, timeout=60,
        )
        return r.returncode == 0
    except Exception:
        return False


def fix_pytorch_for_p100() -> bool:
    """Reinstall cu118 PyTorch into Comfy venv — includes sm_60 (Tesla P100)."""
    py = _comfy_python()
    log.warning("P100: reinstalling PyTorch cu118 for sm_60 kernel support...")
    try:
        subprocess.run([py, "-m", "pip", "install", "-q", "-U", "pip"],
                       check=False, timeout=120)
        for packages in (
            ["torch", "torchvision"],
            ["torchaudio==2.7.1+cu118"],
        ):
            r = subprocess.run(
                [py, "-m", "pip", "install", "--force-reinstall", *packages,
                 "--index-url", "https://download.pytorch.org/whl/cu118"],
                capture_output=True, text=True, timeout=900,
            )
            if r.returncode != 0:
                log.error(f"P100 torch reinstall failed: {(r.stderr or r.stdout)[-800:]}")
                return False
        if not pytorch_cuda_kernel_works(py, include_torchaudio=True):
            log.error("P100 torch reinstall finished but CUDA/torchaudio smoke test still fails")
            return False
        log.info("P100 PyTorch cu118 reinstall OK")
        return True
    except Exception as e:
        log.error(f"P100 torch fix exception: {e}")
        return False


_p100_torch_ready = False


async def ensure_p100_pytorch(args) -> bool:
    """One-shot P100 torch fix before Comfy/agent work."""
    global _p100_torch_ready
    if _p100_torch_ready:
        return True
    if not is_p100_gpu():
        _p100_torch_ready = True
        return True
    if pytorch_cuda_kernel_works():
        log.info("P100 PyTorch CUDA OK")
        _p100_torch_ready = True
        return True
    ok = await asyncio.to_thread(fix_pytorch_for_p100)
    if ok:
        log.info("P100 torch fixed — restarting ComfyUI")
        await ensure_comfyui_running(args, allow_restart=True)
        _p100_torch_ready = True
    return ok


_comfy_last_restart = 0.0
_comfy_dir_cache: Optional[Path] = None


async def comfyui_is_healthy(comfyui_url: str) -> bool:
    """True when ComfyUI responds with HTTP 2xx on /."""
    try:
        headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
        async with aiohttp.ClientSession() as session:
            async with session.get(
                comfyui_url.rstrip("/") + "/",
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=5),
            ) as r:
                return 200 <= r.status < 300
    except Exception:
        return False


def _find_comfy_dir(args) -> Optional[Path]:
    global _comfy_dir_cache
    if _comfy_dir_cache:
        try:
            if (_comfy_dir_cache / "main.py").is_file() or (_comfy_dir_cache / "models").is_dir():
                return _comfy_dir_cache
        except OSError:
            _comfy_dir_cache = None
    try:
        from loboforge_worker.paths import find_comfy_dir
        found = find_comfy_dir(getattr(args, "comfyui_dir", None), args)
    except ImportError:
        found = None
    except OSError:
        found = None
    if found:
        _comfy_dir_cache = found
        return found
    return None


def _comfy_port_from_url(comfyui_url: str) -> int:
    parsed = urlparse(comfyui_url)
    if parsed.port:
        return parsed.port
    return 8188


def _manages_comfy(args) -> bool:
    """Cloud boxes self-heal Comfy via tmux; local agents expect you to start it."""
    if _is_skip_comfy():
        return False
    return getattr(args, "manage_comfy", True)


def _is_native_executor() -> bool:
    try:
        from loboforge_worker.executor_mode import is_native_executor
        return is_native_executor()
    except ImportError:
        return os.environ.get("LOBO_EXECUTOR", "").strip().lower() == "native"


def _is_skip_comfy() -> bool:
    try:
        from loboforge_worker.executor_mode import is_skip_comfy
        return is_skip_comfy()
    except ImportError:
        return (
            os.environ.get("LOBO_EXECUTOR", "").strip().lower() == "native"
            or os.environ.get("LOBO_SKIP_COMFY", "").strip() == "1"
        )


async def _native_ltx_inventory() -> dict:
    try:
        from loboforge_worker.models_sync import build_native_ltx_inventory
        return build_native_ltx_inventory()
    except Exception as ex:
        log.debug("native inventory unavailable: %s", ex)
        return {}


def _start_comfyui_tmux(comfy_dir: Path, port: int) -> None:
    """Bypass supervisord crash-loops — same pattern as provision_gpu.sh."""
    if shutil.which("tmux") is None:
        log.error("tmux not found — cannot start ComfyUI")
        return
    log.warning(f"Starting ComfyUI manually in tmux on port {port}...")
    if shutil.which("supervisorctl"):
        subprocess.run(["supervisorctl", "stop", "comfyui"], capture_output=True, timeout=10)
    subprocess.run(["tmux", "kill-session", "-t", "comfyui"], capture_output=True, timeout=10)
    time.sleep(1)
    venv = ". /venv/main/bin/activate" if Path("/venv/main/bin/activate").exists() else "true"
    cmd = (
        f"cd '{comfy_dir}' && {venv} && "
        f"LD_PRELOAD=libtcmalloc_minimal.so.4 python main.py "
        f"--disable-auto-launch --port {port} --listen 127.0.0.1 --enable-cors-header "
        f"2>&1 | tee /tmp/comfyui.log"
    )
    subprocess.run(["tmux", "new-session", "-d", "-s", "comfyui", cmd], timeout=15)


async def ensure_comfyui_running(args, allow_restart: bool = True) -> bool:
    """Ensure ComfyUI is up; restart via tmux when down (rate-limited)."""
    global _comfy_last_restart
    if await comfyui_is_healthy(args.comfyui_http):
        return True
    if not allow_restart or not _manages_comfy(args):
        if not _manages_comfy(args):
            log.warning(
                "ComfyUI not responding at %s — start it yourself (local agent)",
                args.comfyui_http,
            )
        return False

    loop = asyncio.get_running_loop()
    now = loop.time()
    if now - _comfy_last_restart < COMFY_RESTART_COOLDOWN:
        log.warning("ComfyUI down but restart cooldown active")
        return False

    comfy_dir = _find_comfy_dir(args)
    if not comfy_dir:
        log.error("ComfyUI not responding and ComfyUI dir not found — cannot self-heal")
        return False

    port = _comfy_port_from_url(args.comfyui_http)
    await loop.run_in_executor(None, _start_comfyui_tmux, comfy_dir, port)
    _comfy_last_restart = now

    deadline = now + COMFY_STARTUP_WAIT
    while loop.time() < deadline:
        await asyncio.sleep(5)
        if await comfyui_is_healthy(args.comfyui_http):
            log.info("ComfyUI healthy after manual restart")
            return True
    log.error("ComfyUI failed to respond after manual restart")
    return False


async def load_models_with_retry(args, max_wait: float = 30.0) -> dict:
    """Query model inventory; restart ComfyUI if still empty after a short wait."""
    models = await get_available_models(args.comfyui_http)
    waited = 0.0
    while sum(len(v) for v in models.values()) == 0 and waited < max_wait:
        log.warning("ComfyUI returned 0 models — waiting 10s for index to load...")
        await asyncio.sleep(10)
        waited += 10
        models = await get_available_models(args.comfyui_http)

    if sum(len(v) for v in models.values()) == 0 and _manages_comfy(args):
        log.warning("Still 0 models — attempting ComfyUI restart")
        if await ensure_comfyui_running(args):
            await asyncio.sleep(10)
            models = await get_available_models(args.comfyui_http)
    return models


def model_count(models: dict) -> int:
    return sum(len(v) for v in models.values())


async def comfy_watchdog(args, agent_state: dict, session: "ServerWsSession") -> None:
    """Background self-heal: restart ComfyUI when idle, refresh model inventory."""
    if _is_skip_comfy():
        return
    while True:
        await asyncio.sleep(COMFY_HEALTH_INTERVAL)
        busy = agent_state.get("current_job") is not None or agent_state.get("comfy_external_busy")
        if not busy or get_disk_free_mb() < 2048:
            maybe_free_comfy_disk(args)
        healthy = await comfyui_is_healthy(args.comfyui_http)

        if not healthy:
            if busy:
                log.warning("ComfyUI unhealthy during active job — deferring restart")
                agent_state["comfy_needs_restart"] = True
                continue
            if not _manages_comfy(args):
                continue
            if not await ensure_comfyui_running(args):
                continue
            healthy = True

        if agent_state.pop("comfy_needs_restart", False) and not busy and _manages_comfy(args):
            await ensure_comfyui_running(args)

        if healthy and not busy and model_count(agent_state.get("models", {})) == 0:
            models = await get_available_models(args.comfyui_http)
            if model_count(models) > 0:
                agent_state["models"] = models
                known = agent_state.setdefault("known_loras", [])
                for name in models.get("loras", []):
                    if name and name not in known:
                        known.append(name)
                try:
                    await session.send_json({
                        "type":      "models_update",
                        "node_uuid": args.node_uuid,
                        "models":    models,
                    })
                except Exception:
                    break


async def _fetch_comfy_model_folder(session, comfyui_url: str, folder: str, headers: dict) -> list[str]:
    try:
        async with session.get(
            f"{comfyui_url}/models/{folder}",
            headers=headers,
            timeout=aiohttp.ClientTimeout(total=5),
        ) as r:
            data = await r.json()
            return [v for v in data if isinstance(v, str) and v] if isinstance(data, list) else []
    except Exception:
        return []


def _merge_model_names(target: list[str], names: list[str]) -> None:
    for name in names:
        if name and name not in target:
            target.append(name)


async def get_available_models(comfyui_url: str) -> dict:
    """
    Query ComfyUI for all available models across all loader types.
    Returns a dict of lists — full paths as ComfyUI knows them.
    """
    if _is_skip_comfy():
        inv = await _native_ltx_inventory()
        if inv:
            return inv

    result = {
        "unets": [],
        "checkpoints": [],
        "loras": [],
        "vaes": [],
        "clips": [],
        "ggufs": [],
        "text_encoders": [],
        "latent_upscale_models": [],
    }
    queries = [
        ("UNETLoader",             "unet_name",  "unets"),
        ("CheckpointLoaderSimple", "ckpt_name",  "checkpoints"),
        ("LoraLoader",             "lora_name",  "loras"),
        ("LoraLoaderModelOnly",    "lora_name",  "loras"),
        ("VAELoader",              "vae_name",   "vaes"),
        ("CLIPLoader",             "clip_name",  "clips"),
        ("LoaderGGUF",             "gguf_name",  "ggufs"),
    ]
    folder_keys = {
        "checkpoints": "checkpoints",
        "loras": "loras",
        "vae": "vaes",
        "clip": "clips",
        "text_encoders": "text_encoders",
        "latent_upscale_models": "latent_upscale_models",
        "diffusion_models": "unets",
        "unet": "unets",
        "gguf": "ggufs",
    }
    try:
        headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
        async with aiohttp.ClientSession() as session:
            for node_type, param_name, key in queries:
                try:
                    async with session.get(
                        f"{comfyui_url}/object_info/{node_type}",
                        headers=headers,
                        timeout=aiohttp.ClientTimeout(total=5)
                    ) as r:
                        data = await r.json()
                        node   = data.get(node_type, {})
                        inputs = node.get("input", {}).get("required", {})
                        values = inputs.get(param_name, [[]])[0]
                        if isinstance(values, list):
                            _merge_model_names(result[key], values)
                except Exception:
                    pass
            for folder, key in folder_keys.items():
                _merge_model_names(result[key], await _fetch_comfy_model_folder(session, comfyui_url, folder, headers))
    except Exception as e:
        log.warning(f"Could not query ComfyUI model list: {e}")
    log.info(
        f"Models found — UNETs:{len(result['unets'])} Checkpoints:{len(result['checkpoints'])} "
        f"LoRAs:{len(result['loras'])} VAEs:{len(result['vaes'])} CLIPs:{len(result['clips'])} "
        f"GGUFs:{len(result['ggufs'])}"
    )
    return result


# ── ComfyUI interaction ────────────────────────────────────────────────────────



def _ref_fetch_auth_headers(url: str, secret: str) -> dict:
    """Bearer auth for EventForge job inputs; worker secret for LoboForge API routes."""
    path = (urlparse(url).path or "").lower()
    if "/v1/jobs/" in path and "/input/" in path:
        ef_key = (os.environ.get("EVENT_FORGE_WORKER_KEY") or "").strip()
        if ef_key:
            return {"Authorization": f"Bearer {ef_key}"}
    if _ref_fetch_needs_auth(url):
        return {
            "X-Agent-Secret": secret,
            "Authorization": f"Bearer {secret}",
        }
    return {}

def _ref_fetch_needs_auth(url: str) -> bool:
    """Only LoboForge API agent routes require worker secret — not public CDN URLs."""
    path = url.split("?", 1)[0].lower()
    return "/api/agent/" in path


def _looks_like_image(data: bytes) -> bool:
    if len(data) < 12:
        return False
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return True
    if data[:3] == b"\xff\xd8\xff":
        return True
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return True
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return True
    return False


def _guess_image_ext(data: bytes, url: str) -> tuple[str, str]:
    if data[:8] == b"\x89PNG\r\n\x1a\n":
        return ".png", "image/png"
    if data[:3] == b"\xff\xd8\xff":
        return ".jpg", "image/jpeg"
    if data[:4] == b"RIFF" and data[8:12] == b"WEBP":
        return ".webp", "image/webp"
    if data[:6] in (b"GIF87a", b"GIF89a"):
        return ".gif", "image/gif"
    ext = Path(url.split("?")[0]).suffix.lower()
    if ext in (".png", ".jpg", ".jpeg", ".webp", ".gif"):
        ct = "image/jpeg" if ext in (".jpg", ".jpeg") else f"image/{ext.lstrip('.')}"
        return ext, ct
    return ".jpg", "image/jpeg"


def _agent_storage_url(server_base: str, secret: str, s3_key: str) -> str:
    http_base = server_base.replace("wss://", "https://").replace("ws://", "http://").rstrip("/")
    key = (s3_key or "").lstrip("/")
    url = f"{http_base}/api/agent/storage/{quote(key, safe='/')}"
    if secret:
        url += f"?secret={quote(secret, safe='')}"
    return url


def _storage_key_from_path(path: str) -> str:
    """Map CDN/www paths to S3 keys allowed by /api/agent/storage."""
    p = unquote(path or "").lstrip("/")
    if not p:
        return ""
    lower = p.lower()
    for marker in ("generated/", "uploads/", "caption/"):
        idx = lower.find(marker)
        if idx >= 0:
            return p[idx:]
    return ""


def _extra_ref_fetch_urls(full_url: str, server_base: str, secret: str) -> list[str]:
    """Agent/S3 proxies when CDN or www /uploads paths fail from GPU fleet."""
    extras: list[str] = []
    http_base = server_base.replace("wss://", "https://").replace("ws://", "http://").rstrip("/")
    raw = (full_url or "").strip()
    if not raw:
        return extras
    parsed = urlparse(raw if raw.startswith("http") else f"https://x{raw}")
    path = unquote(parsed.path or raw)
    host = (parsed.netloc or "").lower()
    key = _storage_key_from_path(path)
    if key and (host == "cdn.loboforge.com" or path.startswith("/generated/") or "generated/" in path.lower()):
        extras.append(_agent_storage_url(server_base, secret, key))
    elif path.startswith("/generated/"):
        key = path.lstrip("/")
        if key.startswith("generated/"):
            extras.append(_agent_storage_url(server_base, secret, key))
    if path.startswith("/uploads/"):
        upload_id = Path(path).stem
        if len(upload_id) == 32 and upload_id.isalnum():
            u = f"{http_base}/api/agent/ref-image/{upload_id}"
            if secret:
                u += f"?secret={quote(secret, safe='')}"
            extras.append(u)
    return extras


def _encode_url_path(url: str) -> str:
    """Percent-encode path segments (spaces in CDN filenames break some fetchers).

    Presigned S3 URLs already encode path segments and sign the exact request —
    re-encoding %20 as %2520 invalidates the signature (403 Forbidden).
    """
    parts = urlparse(url)
    if parts.query and "amz-" in parts.query.lower():
        return url
    path = "/".join(quote(unquote(seg), safe="") for seg in parts.path.split("/"))
    return urlunparse((parts.scheme, parts.netloc, path, parts.params, parts.query, parts.fragment))


async def download_ref_image_bytes(image_url: str, server_base: str, secret: str,
                                    fallback_url: str = "") -> Optional[bytes]:
    """Fetch ref image bytes from primary URL or CDN fallback."""
    http_base = server_base.replace("wss://", "https://").replace("ws://", "http://")
    candidates: list[str] = []
    for raw in (image_url, fallback_url):
        raw = (raw or "").strip()
        if not raw:
            continue
        full = raw if raw.startswith("http") else f"{http_base}{raw}"
        if full.startswith("http"):
            full = _encode_url_path(full)
        if full not in candidates:
            candidates.append(full)
        for extra in _extra_ref_fetch_urls(full, server_base, secret):
            if extra not in candidates:
                candidates.append(extra)

    last_err: Optional[Exception] = None
    try:
        async with aiohttp.ClientSession() as session:
            for full_url in candidates:
                headers = _ref_fetch_auth_headers(full_url, secret)
                try:
                    async with session.get(
                        full_url,
                        headers=headers,
                        timeout=aiohttp.ClientTimeout(total=60),
                    ) as r:
                        r.raise_for_status()
                        data = await r.read()
                    if not _looks_like_image(data):
                        preview = data[:120].decode("utf-8", errors="replace")
                        log.warning(
                            "Ref fetch %s returned non-image (%d bytes): %s",
                            full_url[:140], len(data), preview[:80],
                        )
                        continue
                    return data
                except Exception as e:
                    last_err = e
                    log.warning("Ref fetch failed %s: %s", full_url[:140], e)
    except Exception as e:
        last_err = e

    log.error("Failed to download ref image after %d URL(s): %s", len(candidates), last_err)
    return None


async def upload_ref_image_to_comfyui(image_url: str, comfyui_url: str,
                                       server_base: str, secret: str,
                                       fallback_url: str = "") -> Optional[str]:
    """Download a ref image and upload it to ComfyUI input folder.

    Tries the primary URL (usually authenticated API proxy), then an optional
    public CDN fallback. Auth headers are only sent to /api/agent/* routes —
    sending Bearer tokens to CDN URLs returns HTML error pages that ComfyUI
    rejects as corrupt images.
    """
    data = await download_ref_image_bytes(image_url, server_base, secret, fallback_url)
    if not data:
        return None

    try:
        async with aiohttp.ClientSession() as session:
            ext, content_type = _guess_image_ext(data, image_url)
            filename = uuid.uuid4().hex + ext
            form = aiohttp.FormData()
            form.add_field("image", data, filename=filename, content_type=content_type)

            upload_headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
            async with session.post(
                f"{comfyui_url}/upload/image",
                data=form,
                headers=upload_headers,
                timeout=aiohttp.ClientTimeout(total=30),
            ) as r:
                r.raise_for_status()
                result = await r.json()
                name = result.get("name", filename)
                log.info("Ref image uploaded to ComfyUI as: %s (from %s)", name, image_url[:120])
                return name
    except Exception as e:
        log.error("Failed to upload ref image to ComfyUI: %s", e)
        return None


async def submit_prompt(graph: dict, comfyui_url: str, client_id: str) -> Optional[str]:
    """Submit a workflow graph to ComfyUI. Returns prompt_id, or None on
    failure. On failure, _comfy_last_error["__submit__"] is populated with
    the real reason (HTTP body for /prompt rejections, exception text for
    network errors) so the caller can include it in the job_failed status
    instead of the generic 'did not produce output' string."""
    payload = {"client_id": client_id, "prompt": graph}
    try:
        headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
        async with aiohttp.ClientSession() as session:
            async with session.post(
                f"{comfyui_url}/prompt",
                json=payload,
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=30)
            ) as r:
                if not r.ok:
                    body = await r.text()
                    log.error(f"ComfyUI submit failed {r.status}: {body[:500]}")
                    _comfy_last_error["__submit__"] = f"HTTP {r.status}: {body[:300]}"
                    return None
                data = await r.json()
                prompt_id = data.get("prompt_id")
                log.info(f"Submitted to ComfyUI — prompt_id: {prompt_id}")
                return prompt_id
    except Exception as e:
        log.error(f"ComfyUI submit failed: {e}")
        _comfy_last_error["__submit__"] = f"submit exception: {e}"
        return None


async def poll_history(prompt_id: str, comfyui_url: str, token: str = "") -> Optional[dict]:
    """Check ComfyUI history for completed outputs."""
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    try:
        async with aiohttp.ClientSession() as session:
            async with session.get(
                f"{comfyui_url}/history/{prompt_id}",
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=10)
            ) as r:
                if r.status == 401:
                    log.warning(f"History poll 401 — check ComfyUI token")
                    return None
                if r.status == 404:
                    return None
                data = await r.json()
                entry = data.get(prompt_id, {})
                outputs = entry.get("outputs", {})
                for node_id, node_out in outputs.items():
                    for key in ("videos", "audio", "images"):
                        if key in node_out and node_out[key]:
                            return node_out[key][0]  # first file
        return None
    except Exception as e:
        log.warning(f"History poll failed: {e}")
        return None


async def get_comfy_queue(comfyui_url: str) -> tuple[list, list]:
    """Return (queue_running, queue_pending) from ComfyUI."""
    headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
    try:
        async with aiohttp.ClientSession() as session:
            async with session.get(
                f"{comfyui_url}/queue",
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=10),
            ) as r:
                if not r.ok:
                    return [], []
                data = await r.json()
                return data.get("queue_running", []), data.get("queue_pending", [])
    except Exception as e:
        log.warning(f"ComfyUI queue fetch failed: {e}")
        return [], []


def _comfy_queue_prompt_id(entry) -> str | None:
    if isinstance(entry, (list, tuple)) and len(entry) > 1:
        return str(entry[1])
    return None


async def is_comfy_externally_busy(comfyui_url: str) -> bool:
    """
    True when ComfyUI has running or pending prompts this agent is not tracking
    (private/local UI jobs). Fleet work uses _comfy_listeners for its prompts.
    """
    running, pending = await get_comfy_queue(comfyui_url)
    tracked = set(_comfy_listeners.keys())
    running_ids = {_comfy_queue_prompt_id(e) for e in running} - {None}
    pending_ids = {_comfy_queue_prompt_id(e) for e in pending} - {None}
    return bool((running_ids - tracked) or (pending_ids - tracked))


def agent_fleet_busy(state: dict) -> bool:
    """Whether this node should appear busy to LoboForge (no new dispatch)."""
    if state.get("current_job") is not None:
        return True
    return bool(state.get("comfy_external_busy"))


async def refresh_comfy_external_busy(args, state: dict) -> None:
    """Poll Comfy queue; set/clear comfy_external_busy when enabled on this box."""
    if not state.get("block_fleet_when_comfy_busy"):
        if state.pop("comfy_external_busy", None):
            log.info("ComfyUI private work finished — resuming fleet dispatch")
        return

    # Fleet job owns Comfy — don't treat its queue entries as private UI work.
    if state.get("current_job"):
        if state.pop("comfy_external_busy", None):
            log.info("ComfyUI fleet job active — cleared private-busy flag")
        return

    external = await is_comfy_externally_busy(args.comfyui_http)
    prev = state.get("comfy_external_busy")
    if external == prev:
        return
    state["comfy_external_busy"] = external
    if external:
        log.info("ComfyUI busy with private/local work — marking node busy (no fleet jobs)")
    else:
        log.info("ComfyUI idle — node available for fleet dispatch again")


async def clear_comfy_pending_queue(comfyui_url: str) -> None:
    headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(
                f"{comfyui_url}/queue",
                json={"clear": True},
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=10),
            ) as r:
                if not r.ok:
                    body = await r.text()
                    log.warning(f"ComfyUI queue clear failed {r.status}: {body[:200]}")
    except Exception as e:
        log.warning(f"ComfyUI queue clear failed: {e}")


async def interrupt_comfy(comfyui_url: str) -> None:
    headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
    try:
        async with aiohttp.ClientSession() as session:
            async with session.post(
                f"{comfyui_url}/interrupt",
                headers=headers,
                timeout=aiohttp.ClientTimeout(total=10),
            ) as r:
                if not r.ok:
                    body = await r.text()
                    log.warning(f"ComfyUI interrupt failed {r.status}: {body[:200]}")
    except Exception as e:
        log.warning(f"ComfyUI interrupt failed: {e}")


async def reset_comfy_queue(comfyui_url: str, reason: str) -> None:
    """Stop any in-flight run and drop orphaned pending prompts."""
    running, pending = await get_comfy_queue(comfyui_url)
    if not running and not pending:
        return
    log.warning(
        "Resetting Comfy queue (%s): %d running, %d pending",
        reason, len(running), len(pending),
    )
    if running:
        await interrupt_comfy(comfyui_url)
    if pending:
        await clear_comfy_pending_queue(comfyui_url)


# ── Persistent ComfyUI WebSocket ──────────────────────────────────────────────
# One connection shared across all jobs. Events are dispatched to whichever
# job is currently active via the _comfy_listeners dict.

_comfy_ws          = None          # the live websocket
_comfy_client_id   = uuid.uuid4().hex
_comfy_token       = None          # set from args at startup
_comfy_listeners   = {}            # prompt_id -> asyncio.Queue
_comfy_reader_task = None          # single persistent reader task; cancelled on reconnect


async def comfy_ws_reader(comfyui_ws_url: str, comfyui_token: str = None):
    """
    Persistent loop — stays connected to ComfyUI WS forever.
    Reconnects automatically if the connection drops.
    Dispatches all events to per-job queues in _comfy_listeners.
    """
    global _comfy_ws
    uri = f"{comfyui_ws_url}/ws?clientId={_comfy_client_id}"

    while True:
        try:
            log.info(f"Connecting to ComfyUI WS (persistent, client_id={_comfy_client_id[:8]}...)")
            extra_headers = {}
            if _comfy_token:
                extra_headers["Authorization"] = f"Bearer {_comfy_token}"
            async with websockets.connect(uri, ping_interval=WS_PING_INTERVAL, ping_timeout=WS_PING_TIMEOUT,
                                          max_size=16 * 1024 * 1024,   # 16 MB — ComfyUI sends b64 preview frames
                                          additional_headers=extra_headers) as ws:
                _comfy_ws = ws
                log.info("ComfyUI WS connected")

                async for raw in ws:
                    if isinstance(raw, bytes):
                        continue
                    try:
                        msg   = json.loads(raw)
                        mtype = msg.get("type", "")
                        data  = msg.get("data", {})
                        pid   = data.get("prompt_id", "")

                        log.debug(f"ComfyUI: {mtype} pid={pid[:8] if pid else '-'}")

                        if pid and pid in _comfy_listeners:
                            await _comfy_listeners[pid].put(msg)
                        elif not pid and mtype == "status":
                            # Do not broadcast progress/executing without prompt_id — that
                            # kept stuck jobs alive by resetting silence timers on unrelated events.
                            for q in _comfy_listeners.values():
                                await q.put(msg)
                    except Exception:
                        continue

        except Exception as e:
            log.warning(f"ComfyUI WS dropped: {e} — reconnecting in 2s")
            _comfy_ws = None
            await asyncio.sleep(2)


# Per-prompt failure-reason capture. submit_and_wait stores the real
# ComfyUI error string (or HTTP body for submit-time rejections) keyed by
# prompt_id so the caller can include it in the job_failed message instead
# of the generic "ComfyUI did not produce output" string. Keys are popped
# after read to keep this dict from growing unbounded over the agent's life.
_comfy_last_error: dict = {}
# Tracks the most recent prompt_id submit_and_wait worked on. The caller
# uses this as the dict key to retrieve the per-prompt execution_error,
# since the file_info return value alone doesn't carry the prompt_id back.
_comfy_last_error_last_prompt: str = ""

# Single in-flight LoboForge job — survives WS reconnect so we can cancel orphans.
_active_job_task: asyncio.Task | None = None
_active_job_uuid: str | None = None


class ServerDisconnected(Exception):
    """Raised when job status cannot be sent because the server WS dropped."""


async def cancel_active_job(reason: str, *, job_uuid: str | None = None) -> None:
    """Cancel the one tracked run_pulled_job task (and its submit_and_wait loop)."""
    global _active_job_task, _active_job_uuid
    task = _active_job_task
    active_uuid = _active_job_uuid
    if task is None or task.done():
        return
    if job_uuid is not None and active_uuid != job_uuid:
        return
    _active_job_task = None
    _active_job_uuid = None
    label = active_uuid or "?"
    log.warning("Cancelling active job %s (%s)", label[:8] + "…" if len(label) > 8 else label, reason)
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass
    except Exception as e:
        log.debug("Active job task ended with %s after cancel", e)


async def prepare_comfy_for_job(comfyui_url: str, reason: str) -> None:
    """
    Drop orphan Comfy queue entries before a new job.
    Skip reset when the only running prompt is one we're actively listening on.
    """
    running, pending = await get_comfy_queue(comfyui_url)
    tracked = set(_comfy_listeners.keys())
    running_ids = {_comfy_queue_prompt_id(e) for e in running} - {None}
    pending_ids = {_comfy_queue_prompt_id(e) for e in pending} - {None}
    orphan_running = running_ids - tracked
    orphan_pending = pending_ids - tracked

    if not orphan_running and not orphan_pending:
        if running_ids and running_ids <= tracked:
            log.info(
                "Comfy queue has tracked prompt(s) — skip reset (%s)",
                reason,
            )
        return

    log.warning(
        "Resetting Comfy queue (%s): %d orphan running, %d orphan pending "
        "(%d tracked listener(s))",
        reason,
        len(orphan_running),
        len(orphan_pending),
        len(tracked),
    )
    if running:
        await interrupt_comfy(comfyui_url)
    if pending:
        await clear_comfy_pending_queue(comfyui_url)


async def submit_and_wait(
    graph: dict,
    comfyui_url: str,
    on_progress,
    *,
    max_total_sec: int | None = None,
    on_activity=None,
) -> Optional[dict]:
    """
    Submit a prompt using the persistent ComfyUI client_id and wait for
    completion events via the shared WS reader. On failure, leaves a
    diagnostic string in _comfy_last_error keyed by prompt_id (or
    "__submit__" if submit itself failed) — the caller can pop it to
    enrich the job_failed status sent back to LoboForge.
    """
    global _comfy_last_error_last_prompt

    prompt_id = None
    # Orphan prompts from timed-out jobs pile up in Comfy; don't interrupt a
    # prompt we're still listening on (e.g. after a server WS blip).
    await prepare_comfy_for_job(comfyui_url, "before new job")

    prompt_id = await submit_prompt(graph, comfyui_url, _comfy_client_id)
    if not prompt_id:
        # submit_prompt already populated _comfy_last_error["__submit__"]
        # with the real HTTP body / exception text; caller pops it below.
        _comfy_last_error_last_prompt = ""
        return None
    _comfy_last_error_last_prompt = prompt_id

    log.info(f"Submitted prompt {prompt_id}, waiting for completion...")

    queue = asyncio.Queue()
    _comfy_listeners[prompt_id] = queue

    loop = asyncio.get_event_loop()
    submitted_at = loop.time()
    exec_started_at = None
    wall_limit = max_total_sec or 150
    # Cap time sitting in Comfy's queue (should be near-zero after reset above).
    queue_wait_limit = max(wall_limit * 2, 300)
    failed = True
    last_comfy_event_at = loop.time()

    try:
        while True:
            now = loop.time()
            if exec_started_at is not None:
                elapsed_exec = int(now - exec_started_at)
                if elapsed_exec > wall_limit:
                    log.error(
                        f"Job {prompt_id} exceeded {wall_limit}s execution wall clock "
                        f"({elapsed_exec}s) — aborting"
                    )
                    _comfy_last_error[prompt_id] = f"job exceeded {wall_limit}s on worker"
                    return None
            else:
                queue_wait = int(now - submitted_at)
                if queue_wait > queue_wait_limit:
                    log.error(
                        f"Job {prompt_id} stuck in Comfy queue for {queue_wait}s "
                        f"(limit {queue_wait_limit}s) — aborting"
                    )
                    _comfy_last_error[prompt_id] = f"stuck in Comfy queue {queue_wait}s"
                    return None

            # Wait for Comfy events; ping server during long silent encode/mux phases.
            try:
                msg = await asyncio.wait_for(queue.get(), timeout=JOB_ACTIVITY_PING)
                last_comfy_event_at = loop.time()
            except asyncio.TimeoutError:
                if on_activity:
                    try:
                        await on_activity()
                    except Exception:
                        pass
                # Comfy sometimes finishes without WS executing/progress (stale reader,
                # reconnect, or fast cache hits) — poll history whenever we're idle.
                if prompt_id:
                    output = await poll_history(prompt_id, comfyui_url, _comfy_token)
                    if output:
                        failed = False
                        log.info(
                            "Job %s complete (history poll%s)",
                            prompt_id,
                            " during silent phase" if exec_started_at is not None else " — missed WS events",
                        )
                        return output
                if loop.time() - last_comfy_event_at > MAX_JOB_WAIT:
                    elapsed = int(loop.time() - submitted_at)
                    log.error(
                        f"Job {prompt_id} — no activity from ComfyUI for {MAX_JOB_WAIT}s "
                        f"(since submit: {elapsed}s). ComfyUI may have crashed."
                    )
                    return None
                continue

            mtype = msg.get("type", "")
            data  = msg.get("data", {})

            if mtype == "progress":
                value = data.get("value", 0)
                maxx  = data.get("max", 1)
                if exec_started_at is not None:
                    elapsed = int(loop.time() - exec_started_at)
                    log.info(f"Progress {prompt_id}: {value}/{maxx} steps ({elapsed}s in exec)")
                else:
                    log.info(f"Progress {prompt_id}: {value}/{maxx} steps (queued)")
                await on_progress(value, maxx)

            elif mtype in ("executing", "execution_success"):
                node = data.get("node")
                if node is not None and exec_started_at is None:
                    exec_started_at = loop.time()
                    log.info(
                        f"Job {prompt_id} execution started "
                        f"(queue wait {int(exec_started_at - submitted_at)}s)"
                    )
                if node is None or mtype == "execution_success":
                    elapsed = int(loop.time() - (exec_started_at or submitted_at))
                    log.info(f"ComfyUI complete: {mtype} for {prompt_id} in {elapsed}s")
                    await asyncio.sleep(0.3)
                    for _ in range(6):
                        output = await poll_history(prompt_id, comfyui_url, _comfy_token)
                        if output:
                            failed = False
                            return output
                        await asyncio.sleep(0.5)
                    log.error(f"Completion event received but no output in history for {prompt_id}")
                    return None

            elif mtype == "execution_error":
                err       = data.get("exception_message", "unknown error")
                node      = data.get("node_id", "?")
                node_type = data.get("node_type", "")
                log.error(f"ComfyUI execution error on node {node} ({node_type}): {err}")
                # Cache the actual reason so the caller can ship it back to
                # LoboForge as the job_failed reason. Keep node identity so
                # admins know WHERE the workflow choked, not just that it did.
                _comfy_last_error[prompt_id] = f"node {node} ({node_type}): {err}"
                return None

            elif mtype == "execution_interrupted":
                log.warning(f"Job {prompt_id} was interrupted in ComfyUI")
                _comfy_last_error[prompt_id] = "execution interrupted by ComfyUI"
                return None

    except asyncio.CancelledError:
        if prompt_id:
            _comfy_last_error[prompt_id] = "job cancelled"
        raise

    finally:
        if prompt_id:
            _comfy_listeners.pop(prompt_id, None)
            if failed:
                reason = _comfy_last_error.get(prompt_id, "")
                if reason == "job cancelled":
                    log.info("Skipping Comfy queue reset — job was cancelled externally")
                else:
                    await reset_comfy_queue(comfyui_url, f"job {prompt_id} failed")


async def download_output(file_info: dict, comfyui_url: str) -> Optional[bytes]:
    """Download a completed output file from ComfyUI."""
    filename  = file_info.get("filename", "")
    subfolder = file_info.get("subfolder", "")
    ftype     = file_info.get("type", "output")

    if not filename:
        return None

    url = (f"{comfyui_url}/view"
           f"?filename={filename}"
           f"&subfolder={subfolder}"
           f"&type={ftype}")
    try:
        headers = {"Authorization": f"Bearer {_comfy_token}"} if _comfy_token else {}
        async with aiohttp.ClientSession() as session:
            async with session.get(url, headers=headers, timeout=aiohttp.ClientTimeout(total=120)) as r:
                r.raise_for_status()
                data = await r.read()
                log.info(f"Downloaded output: {filename} ({len(data):,} bytes)")
                return data
    except Exception as e:
        log.error(f"Failed to download output {filename}: {e}")
        return None


# ── WD14 auto-tagging helper ──────────────────────────────────────────────────

async def _tag_image_async(data: bytes, mime_type: str) -> Optional[dict]:
    """
    Run WD14 in a worker thread so the asyncio loop isn't blocked.
    Returns the tag result dict, or None if WD14 is unavailable / failed.
    """
    if not WD14_AVAILABLE or wd14_tagger is None:
        return None
    if not mime_type or not mime_type.startswith("image/"):
        return None
    try:
        loop = asyncio.get_event_loop()
        result = await loop.run_in_executor(None, wd14_tagger.tag_image_bytes, data)
        if isinstance(result, dict) and "error" in result:
            log.warning(f"WD14 tagging error: {result.get('error')}")
            return None
        return result
    except Exception as e:
        log.warning(f"WD14 tagging exception: {e}")
        return None


# ── Server WebSocket session (single recv loop, routed replies) ────────────────

class ServerWsSession:
    """
    Only the main `async for raw in ws` loop may call recv(). File transfers and
    other handlers wait on Futures that the main loop fulfills via try_deliver().
    """

    def __init__(self, ws):
        self.ws = ws
        self._send_lock = asyncio.Lock()
        self._waiter: asyncio.Future | None = None
        self._wait_types: frozenset[str] = frozenset()

    async def send_json(self, data: dict) -> bool:
        try:
            async with self._send_lock:
                await self.ws.send(json.dumps(data))
            return True
        except (
            websockets.exceptions.ConnectionClosed,
            websockets.exceptions.ConnectionClosedError,
            websockets.exceptions.ConnectionClosedOK,
        ) as e:
            log.warning(
                "Server WS closed while sending %s: %s",
                data.get("type", "?"),
                e,
            )
            return False

    async def send_bytes(self, data: bytes) -> None:
        async with self._send_lock:
            await self.ws.send(data)

    async def wait_response(self, types: set[str], timeout: float) -> dict:
        if self._waiter is not None and not self._waiter.done():
            raise RuntimeError("Concurrent server WS wait")
        loop = asyncio.get_running_loop()
        self._waiter = loop.create_future()
        self._wait_types = frozenset(types)
        try:
            return await asyncio.wait_for(self._waiter, timeout)
        finally:
            self._waiter = None
            self._wait_types = frozenset()

    def try_deliver(self, msg: dict) -> bool:
        if self._waiter is None or self._waiter.done():
            return False
        mtype = msg.get("type", "")
        if mtype in self._wait_types:
            self._waiter.set_result(msg)
            return True
        return False

    @property
    def waiting(self) -> bool:
        return self._waiter is not None and not self._waiter.done()


# ── Chunked file transfer over WebSocket ──────────────────────────────────────

async def send_file(session: ServerWsSession, job_uuid: str, filename: str,
                     data: bytes, mime_type: str) -> bool:
    """
    Send a completed file to the LoboForge server via chunked binary WebSocket frames.

    Protocol:
      1. Send JSON: {type: "file_begin", job_uuid, filename, file_size, mime_type, chunk_count}
      2. Wait for JSON: {type: "file_ready"}
      3. Send N binary frames: [4-byte chunk_index uint32 BE][chunk_data]
      4. Send JSON: {type: "file_end", job_uuid, sha256}
      5. Wait for JSON: {type: "file_ok"} or {type: "file_error"}
    """
    sha256      = hashlib.sha256(data).hexdigest()
    file_size   = len(data)
    chunk_count = (file_size + CHUNK_SIZE - 1) // CHUNK_SIZE

    # Run WD14 auto-tagger (if available, image only). Silent no-op otherwise.
    wd14 = await _tag_image_async(data, mime_type)
    if wd14 is not None:
        tags_preview = ", ".join(wd14.get("tags", [])[:6])
        log.info(
            f"WD14: rating={wd14.get('rating')} nsfw={wd14.get('is_nsfw')} "
            f"tags=[{tags_preview}{'...' if len(wd14.get('tags', [])) > 6 else ''}]"
        )

    # Step 1 — announce file
    file_begin_msg = {
        "type":        "file_begin",
        "job_uuid":    job_uuid,
        "filename":    filename,
        "file_size":   file_size,
        "mime_type":   mime_type,
        "chunk_count": chunk_count,
        "sha256":      sha256,
    }
    if wd14 is not None:
        file_begin_msg["wd14"] = wd14
    await session.send_json(file_begin_msg)

    # Step 2 — wait for file_ready (routed by main recv loop, not ws.recv here).
    try:
        ack = await session.wait_response({"file_ready", "file_error"}, 60.0)
    except asyncio.TimeoutError:
        log.error("Timeout waiting for file_ready (60s deadline)")
        return False

    if ack.get("type") == "file_error":
        log.error(f"Server rejected file_begin: {ack.get('reason', '?')}")
        return False

    log.info(f"Sending {filename} in {chunk_count} chunks ({file_size:,} bytes)")

    # Step 3 — send chunks as binary frames: [4-byte index][data]
    for i in range(chunk_count):
        start  = i * CHUNK_SIZE
        end    = min(start + CHUNK_SIZE, file_size)
        chunk  = data[start:end]
        frame  = struct.pack(">I", i) + chunk
        await session.send_bytes(frame)

        if (i + 1) % 50 == 0 or (i + 1) == chunk_count:
            pct = (i + 1) / chunk_count * 100
            log.info(f"  Transfer: {i+1}/{chunk_count} chunks ({pct:.0f}%)")

    # Step 4 — end marker
    await session.send_json({
        "type":     "file_end",
        "job_uuid": job_uuid,
        "sha256":   sha256,
    })

    try:
        result = await session.wait_response({"file_ok", "file_error"}, 60.0)
    except asyncio.TimeoutError:
        log.error("Timeout waiting for file_ok (60s deadline)")
        return False

    if result.get("type") == "file_ok":
        log.info(f"File transfer confirmed: {filename}")
        return True
    log.error(f"File transfer failed: {result.get('reason', '?')}")
    return False


def guess_mime(filename: str) -> str:
    ext = Path(filename).suffix.lower()
    return {
        ".mp4": "video/mp4",
        ".webm": "video/webm",
        ".mp3": "audio/mpeg",
        ".wav": "audio/wav",
        ".png": "image/png",
        ".jpg": "image/jpeg",
        ".jpeg": "image/jpeg",
    }.get(ext, "application/octet-stream")


# ── LTX graph patching (use whatever LTX assets this box has) ─────────────────

# LTX-only node types — do not include generic loaders (CheckpointLoaderSimple,
# LoraLoaderModelOnly) or Wan/Flux graphs mis-route to patch_ltx_graph_models and
# skip loras/ → Comfy lora_name normalization.
_LTX_GRAPH_NODE_TYPES = frozenset({
    "LTXAVTextEncoderLoader",
    "LTXVAudioVAELoader",
    "LTXVLatentUpsampler",
    "LTXVConditioning",
    "EmptyLTXVLatentVideo",
})


def _model_basename(name: str) -> str:
    return (name or "").replace("\\", "/").split("/")[-1]


def _strip_loras_api_prefix(name: str) -> str:
    """Comfy LoraLoader lists bare filenames; API canonical paths use loras/foo.safetensors."""
    cur = (name or "").strip()
    if not cur:
        return cur
    norm = cur.replace("\\", "/")
    if norm.lower().startswith("loras/"):
        return _model_basename(norm)
    return cur


def _is_ltx_asset_name(name: str) -> bool:
    n = (name or "").lower().replace("\\", "/")
    if not n or "taeltx" in n:
        return False
    return (
        "ltx" in n
        or "ltx23" in n
        or "ltx-2" in n
        or "ltx2" in n
        or "/ltx/" in n
        or n.startswith("ltx/")
    )


def _model_available(available: list[str], current: str) -> bool:
    if not current:
        return False
    cur = current.strip()
    base = _model_basename(cur)
    for name in available:
        if name == cur:
            return True
        if _model_basename(name) == base:
            return True
    return False


def _resolve_comfy_model_name(available: list[str], pick: str) -> str:
    if pick in available:
        return pick
    base = _model_basename(pick)
    for name in available:
        if _model_basename(name) == base:
            return name
    return pick


def _clip_inventory(models: dict) -> list[str]:
    """Merged text-encoder list — ComfyUI lists CLIPs under text_encoders/ and clips/."""
    out: list[str] = []
    for key in ("text_encoders", "clips"):
        for name in models.get(key) or []:
            if isinstance(name, str) and name.strip() and name not in out:
                out.append(name)
    return out


def _clip_type_for_name(name: str) -> str | None:
    n = (name or "").lower()
    if "gpt_oss" in n or "gpt-oss" in n:
        return "lens"
    if "qwen" in n:
        return "flux2"
    if "zimage" in n or "z_image" in n:
        return "lumina2"
    if "t5" in n or "umt5" in n:
        return "flux2"
    return None


def _score_clip_for_type(name: str, clip_type: str) -> int:
    n = (name or "").lower().replace("\\", "/")
    t = (clip_type or "").lower()
    if t == "flux2":
        if "qwen" in n:
            return 200 + (40 if "8b" in n else 0)
        if "umt5" in n or "t5xxl" in n:
            return 80
        return -1000
    if t == "lens":
        if "gpt_oss" in n or "gpt-oss" in n:
            return 200
        return -1000
    if t in ("lumina2", "stable_cascade", "sd3"):
        if "zimage" in n:
            return 200
        return -1000
    # Unknown type — prefer basename match only; mild preference by family.
    score = 0
    if "qwen" in n:
        score += 40
    if "gpt_oss" in n:
        score += 40
    if "zimage" in n:
        score += 40
    return score


def _infer_clip_type(clip_type: str, pick: str) -> str:
    t = (clip_type or "").strip()
    if t:
        return t
    pick_l = (pick or "").lower()
    if "qwen" in pick_l:
        return "flux2"
    if "zimage" in pick_l:
        return "lumina2"
    if "gpt_oss" in pick_l or "gpt-oss" in pick_l:
        return "lens"
    return ""


def _pick_clip_fallback(available: list[str], clip_type: str, pick: str) -> str | None:
    if not available:
        return None
    clip_type = _infer_clip_type(clip_type, pick)
    if not clip_type:
        return None
    scored = [(_score_clip_for_type(n, clip_type), n) for n in available]
    scored = [(s, n) for s, n in scored if s > 0]
    if not scored:
        return None
    scored.sort(key=lambda t: t[0], reverse=True)
    return scored[0][1]


def _resolve_clip_loader_name(
    available: list[str],
    pick: str,
    clip_type: str,
    inputs: dict,
) -> str:
    if _model_available(available, pick):
        return pick
    resolved = _resolve_comfy_model_name(available, pick)
    if _model_available(available, resolved):
        return resolved
    effective_type = _infer_clip_type(clip_type, pick)
    fallback = _pick_clip_fallback(available, effective_type, pick)
    if not fallback:
        return pick
    new_type = _clip_type_for_name(fallback)
    if new_type and isinstance(inputs.get("type"), str) and inputs.get("type") != new_type:
        inputs["type"] = new_type
    return fallback


def _graph_unresolved_clip_loaders(graph: dict, models: dict) -> str | None:
    available = _clip_inventory(models)
    missing: list[str] = []
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        ct = node.get("class_type", "")
        if ct not in ("CLIPLoader", "DualCLIPLoader", "TripleCLIPLoader"):
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        clip_type = inputs.get("type", "")
        if isinstance(clip_type, list):
            clip_type = clip_type[0] if clip_type else ""
        clip_type = str(clip_type or "")
        for key, cur in inputs.items():
            if not key.startswith("clip_name") or not isinstance(cur, str) or not cur:
                continue
            if not _model_available(available, cur):
                missing.append(f"{key}={cur}" + (f" type={clip_type}" if clip_type else ""))
    if missing:
        return "Text encoder not on worker: " + "; ".join(missing)
    return None


# Loader inputs remapped against the box's own ComfyUI inventory. Queue-mode job specs
# carry canonical model names (node-agnostic dispatch) — prod boxes keep models in
# subfolders (e.g. F2Klein/flux-2-klein-9b-fp8.safetensors), so remap by basename.
_GENERIC_LOADER_INPUTS: dict[str, list[tuple[str, str]]] = {
    "UNETLoader": [("unet_name", "unets")],
    "UnetLoaderGGUF": [("unet_name", "ggufs")],
    "CheckpointLoaderSimple": [("ckpt_name", "checkpoints")],
    "LoraLoader": [("lora_name", "loras")],
    "LoraLoaderModelOnly": [("lora_name", "loras")],
    "VAELoader": [("vae_name", "vaes")],
    "CLIPLoader": [("clip_name", "clips")],
    "DualCLIPLoader": [("clip_name1", "clips"), ("clip_name2", "clips")],
    "TripleCLIPLoader": [
        ("clip_name1", "clips"), ("clip_name2", "clips"), ("clip_name3", "clips"),
    ],
    "UpscaleModelLoader": [("model_name", "latent_upscale_models")],
}


def patch_graph_model_names(graph: dict, models: dict) -> dict:
    """Remap loader model names to this box's ComfyUI inventory by basename.
    ComfyUI requires exact inventory paths (e.g. F2Klein/foo.safetensors), not bare
    basenames — queue graphs often carry canonical bare names from the API."""
    clip_inv = _clip_inventory(models)
    patches: list[str] = []
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        class_type = node.get("class_type", "")
        specs = _GENERIC_LOADER_INPUTS.get(class_type)
        inputs = node.get("inputs")
        if not specs or not isinstance(inputs, dict):
            continue
        clip_type = inputs.get("type", "")
        if isinstance(clip_type, list):
            clip_type = clip_type[0] if clip_type else ""
        clip_type = str(clip_type or "")
        for input_name, inv_key in specs:
            cur = inputs.get(input_name)
            if not isinstance(cur, str) or not cur:
                continue
            orig = cur
            if inv_key == "loras":
                cur = _strip_loras_api_prefix(cur)
                if cur != orig:
                    inputs[input_name] = cur
            available = clip_inv if inv_key == "clips" else (models.get(inv_key) or [])
            if cur in available:
                continue
            if inv_key == "clips":
                resolved = _resolve_clip_loader_name(available, cur, clip_type, inputs)
            else:
                resolved = _resolve_comfy_model_name(available, cur)
            if inv_key == "loras" and resolved == cur and cur != _model_basename(cur):
                resolved = _model_basename(cur)
            if resolved != cur:
                inputs[input_name] = resolved
                patches.append(f"{input_name}: {orig} -> {resolved}")
    if patches:
        log.info("Graph model names remapped to local inventory: %s", "; ".join(patches))
    return graph


def _pick_best_ltx(available: list[str], scorer) -> str | None:
    scored = [(scorer(name), name) for name in available if _is_ltx_asset_name(name)]
    if not scored:
        return None
    scored.sort(key=lambda t: t[0], reverse=True)
    if scored[0][0] < 0:
        return None
    return scored[0][1]


def _score_ltx_checkpoint(name: str) -> int:
    n = name.lower().replace("\\", "/")
    if "audio_vae" in n or n.endswith("_vae.safetensors") or "/vae/" in n:
        return -1000
    score = 0
    if "ltx-2.3" in n or "ltx2.3" in n:
        score += 200
    elif "ltx-2" in n or "ltx2" in n:
        score += 120
    if "fp8" in n:
        score += 80
    if "dev" in n and "distilled" not in n:
        score += 40
    if "22b" in n:
        score += 30
    if "transformer" in n:
        score += 20
    if "distilled" in n and "lora" not in n:
        score -= 40
    return score


def _score_ltx_lora(name: str) -> int:
    n = name.lower().replace("\\", "/")
    if "ltx" not in n:
        return -1000
    score = 0
    if "distilled-lora-384" in n:
        score += 200
    if "distilled" in n:
        score += 100
    if "ltx-2.3" in n or "ltx2.3" in n:
        score += 80
    if "pt_" in n or "_pt" in n or "/pt_" in n:
        score -= 60
    return score


def _score_ltx_text_encoder(name: str) -> int:
    n = name.lower()
    if "gemma" not in n:
        return -1000
    score = 0
    if "12b" in n:
        score += 100
    if "fp4" in n:
        score += 80
    elif "fp8" in n:
        score += 40
    return score


def _score_ltx_upscaler(name: str) -> int:
    n = name.lower()
    if "ltx" not in n:
        return -1000
    score = 0
    if "spatial" in n:
        score += 120
    if "ltx-2.3" in n or "ltx2.3" in n:
        score += 80
    if "2.3" in n:
        score += 40
    return score


def graph_has_ltx_nodes(graph: dict) -> bool:
    for node in graph.values():
        if isinstance(node, dict) and node.get("class_type") in _LTX_GRAPH_NODE_TYPES:
            return True
    return False


def patch_ltx_graph_models(graph: dict, models: dict) -> dict:
    """Rewrite hardcoded LTX workflow model names to the best match on this box."""
    if not graph or not graph_has_ltx_nodes(graph):
        return graph

    ckpts = list(models.get("checkpoints") or [])
    loras = list(models.get("loras") or [])
    text_encoders = list(models.get("text_encoders") or []) + list(models.get("clips") or [])
    upscalers = list(models.get("latent_upscale_models") or [])

    best_ckpt = _pick_best_ltx(ckpts, _score_ltx_checkpoint)
    best_lora = _pick_best_ltx(loras, _score_ltx_lora)
    best_te = _pick_best_ltx(text_encoders, _score_ltx_text_encoder)
    best_up = _pick_best_ltx(upscalers, _score_ltx_upscaler)

    if not any((best_ckpt, best_lora, best_te, best_up)):
        return graph

    patches: list[str] = []
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        ct = node.get("class_type", "")
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue

        if ct in ("CheckpointLoaderSimple", "LTXVAudioVAELoader") and best_ckpt:
            cur = inputs.get("ckpt_name")
            if isinstance(cur, str) and cur and not _model_available(ckpts, cur):
                resolved = _resolve_comfy_model_name(ckpts, best_ckpt)
                inputs["ckpt_name"] = resolved
                patches.append(f"{ct}.ckpt_name={resolved}")

        if ct == "LTXAVTextEncoderLoader":
            if best_ckpt:
                cur = inputs.get("ckpt_name")
                if isinstance(cur, str) and cur and not _model_available(ckpts, cur):
                    resolved = _resolve_comfy_model_name(ckpts, best_ckpt)
                    inputs["ckpt_name"] = resolved
                    patches.append(f"text_encoder_loader.ckpt_name={resolved}")
            if best_te:
                cur = inputs.get("text_encoder")
                if isinstance(cur, str) and cur and not _model_available(text_encoders, cur):
                    resolved = _resolve_comfy_model_name(text_encoders, best_te)
                    inputs["text_encoder"] = resolved
                    patches.append(f"text_encoder={resolved}")

        if ct == "LoraLoaderModelOnly" and best_lora:
            cur = inputs.get("lora_name")
            if isinstance(cur, str) and cur and _is_ltx_asset_name(cur) and not _model_available(loras, cur):
                resolved = _resolve_comfy_model_name(loras, best_lora)
                inputs["lora_name"] = resolved
                patches.append(f"lora={resolved}")

        if ct == "LatentUpscaleModelLoader" and best_up:
            cur = inputs.get("model_name")
            if isinstance(cur, str) and cur and not _model_available(upscalers, cur):
                resolved = _resolve_comfy_model_name(upscalers, best_up)
                inputs["model_name"] = resolved
                patches.append(f"upscaler={resolved}")

    if patches:
        log.info("LTX workflow patched for local inventory: %s", "; ".join(patches))
    return graph


# ── Job handler ────────────────────────────────────────────────────────────────

async def _handle_native_ltx_job(
    session: "ServerWsSession",
    job_uuid: str,
    graph: dict,
    send_status,
    state: dict | None,
) -> None:
    """Run LTX via ltx-pipelines DistilledPipeline — no ComfyUI."""
    from loboforge_worker.inference.ltx.runner import run_native_ltx_job

    last_pct = [-1]

    async def on_progress(step, total):
        pct = int(step / total * 100) if total > 0 else 0
        if pct != last_pct[0]:
            last_pct[0] = pct
            try:
                await send_status("job_progress", step=step, total=total, pct=pct)
            except Exception:
                pass

    async def on_activity():
        if last_pct[0] < 0:
            return
        try:
            await send_status("job_progress", step=last_pct[0], total=100, pct=last_pct[0])
        except Exception:
            pass

    loop = asyncio.get_running_loop()

    def on_activity_sync():
        """Runner tick runs in asyncio task; schedule async lease heartbeat safely."""
        try:
            asyncio.run_coroutine_threadsafe(on_activity(), loop).result(timeout=10)
        except Exception:
            pass

    try:
        result = await run_native_ltx_job(
            graph, on_progress=on_progress, on_activity=on_activity_sync
        )
    except Exception as ex:
        log.error("Job %s: native LTX failed — %s", job_uuid, ex)
        await send_status("job_failed", reason=str(ex))
        return

    if not result:
        await send_status("job_failed", reason="native LTX produced no output")
        return

    file_data, filename = result
    mime_type = guess_mime(filename)
    log.info("Job %s: native LTX output %s (%d bytes)", job_uuid, filename, len(file_data))

    ok = await send_file(session, job_uuid, filename, file_data, mime_type)
    if not ok:
        await send_status("job_failed", reason="File transfer to server failed")
        return
    log.info("Job %s complete (native LTX)", job_uuid)


def _ref_graph_key_from_url(ref_url: str) -> str:
    """Match C# ComfyGraphKeys.RefGraphKey for agent-side fallback keys."""
    path = ref_url.split("?")[0]
    name = Path(path).name
    try:
        g = uuid.UUID(name)
        return f"ref_{g.hex}"
    except ValueError:
        return name


def _patch_load_image_key(graph: dict, graph_key: str, comfy_name: str) -> bool:
    """Set LoadImage inputs.image when it equals graph_key (preferred over str.replace)."""
    patched = False
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        if node.get("class_type") != "LoadImage":
            continue
        inputs = node.get("inputs")
        if not isinstance(inputs, dict):
            continue
        if inputs.get("image") == graph_key:
            inputs["image"] = comfy_name
            patched = True
    return patched


async def apply_ref_images_to_graph(graph: dict, ref_images, args) -> tuple:
    """Download each ref image, upload to ComfyUI input/, patch LoadImage nodes."""
    for item in ref_images or []:
        if isinstance(item, dict):
            ref_url = (item.get("url") or item.get("Url") or "").strip()
            graph_key = (item.get("graph_key") or item.get("graphKey") or item.get("GraphKey") or "").strip()
            fallback_url = (item.get("fallback_url") or item.get("fallbackUrl") or item.get("FallbackUrl") or "").strip()
        else:
            ref_url = str(item or "").strip()
            graph_key = ""
            fallback_url = ""

        if not ref_url:
            continue
        if not graph_key:
            graph_key = _ref_graph_key_from_url(ref_url)

        maybe_free_comfy_disk(args)
        if _is_skip_comfy():
            data = await download_ref_image_bytes(ref_url, args.server, args.secret, fallback_url)
            if not data:
                return None, f"Failed to download ref image ({graph_key}) from {ref_url[:160]}"
            ext, _ = _guess_image_ext(data, ref_url)
            ref_dir = Path("/tmp/lobo-ref-images")
            ref_dir.mkdir(parents=True, exist_ok=True)
            local_path = str(ref_dir / f"{uuid.uuid4().hex}{ext}")
            Path(local_path).write_bytes(data)
            patch_name = local_path
            log.info("Saved ref image locally for native executor: %s", local_path)
        else:
            comfy_name = await upload_ref_image_to_comfyui(
                ref_url, args.comfyui_http, args.server, args.secret, fallback_url
            )
            if not comfy_name:
                maybe_free_comfy_disk(args, min_free_mb=4096)
                comfy_name = await upload_ref_image_to_comfyui(
                    ref_url, args.comfyui_http, args.server, args.secret, fallback_url
                )
            if not comfy_name:
                return None, f"Failed to upload ref image ({graph_key}) from {ref_url[:160]}"
            patch_name = comfy_name

        if not _patch_load_image_key(graph, graph_key, patch_name):
            graph_str = json.dumps(graph)
            if graph_key not in graph_str:
                log.warning("Ref graph_key %r not in workflow — skipping", graph_key)
                continue
            graph = json.loads(graph_str.replace(graph_key, patch_name))
        else:
            log.info("Patched LoadImage placeholder %r -> %r", graph_key, patch_name)

    if ref_images:
        for node in graph.values():
            if not isinstance(node, dict) or node.get("class_type") != "LoadImage":
                continue
            inputs = node.get("inputs") or {}
            img = inputs.get("image")
            if isinstance(img, str) and img.startswith("ref_"):
                return None, f"Reference image not applied to workflow (LoadImage still has {img})"

    return graph, None


async def handle_joycaption_job(session: ServerWsSession, msg: dict, args, state: dict | None = None) -> None:
    """Joycaption fleet job — fetch image from presigned S3 URL, return caption text."""
    job_uuid = msg.get("job_uuid", "")
    ref_images = msg.get("ref_images") or []
    image_url = None
    if ref_images:
        first = ref_images[0]
        if isinstance(first, dict):
            image_url = first.get("url") or first.get("fallback_url")
        else:
            image_url = first
    if not image_url:
        await session.send_json({
            "type": "job_failed",
            "job_uuid": job_uuid,
            "reason": "missing ref image url",
        })
        return

    if not JOYCAPTION_AVAILABLE or joycaption_runner is None:
        await session.send_json({
            "type": "job_failed",
            "job_uuid": job_uuid,
            "reason": "joycaption not available on this agent",
        })
        return

    await session.send_json({"type": "job_started", "job_uuid": job_uuid})
    if state is not None:
        state["job_started"] = True

    try:
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=120)) as http:
            async with http.get(image_url) as resp:
                if resp.status != 200:
                    raise RuntimeError(f"fetch failed: HTTP {resp.status}")
                image_bytes = await resp.read()
                mime = resp.content_type or "image/jpeg"

        loop = asyncio.get_event_loop()
        result = await loop.run_in_executor(
            None, joycaption_runner.caption_image_bytes, image_bytes, mime)

        if result.get("error"):
            await session.send_json({
                "type": "job_failed",
                "job_uuid": job_uuid,
                "reason": result["error"],
            })
            return

        await session.send_json({
            "type": "caption_job_complete",
            "node_uuid": getattr(args, "node_uuid", ""),
            "job_uuid": job_uuid,
            "caption": result.get("caption") or "",
            "external_id": msg.get("external_id") or "",
        })
        log.info("Joycaption job %s complete (%d chars)", job_uuid, len(result.get("caption") or ""))
    except Exception as ex:
        log.exception("Joycaption job %s failed", job_uuid)
        await session.send_json({
            "type": "job_failed",
            "job_uuid": job_uuid,
            "reason": str(ex),
        })


async def handle_job(session: ServerWsSession, msg: dict, args, state: dict | None = None) -> None:
    """Process a single generation job end-to-end."""
    job_uuid  = msg.get("job_uuid", "")
    model     = (msg.get("model") or "").strip().lower()
    if msg.get("caption") or model == "joycaption":
        await handle_joycaption_job(session, msg, args, state)
        return

    graph     = msg.get("graph", {})
    ref_urls  = msg.get("ref_images", [])   # list of paths/URLs or {url, graph_key}
    server_cap = msg.get("max_total_sec")
    try:
        server_cap = int(server_cap) if server_cap is not None else None
    except (TypeError, ValueError):
        server_cap = None
    wall_sec  = resolve_job_wall_seconds(model, graph, server_cap)

    log.info(f"Job {job_uuid} received (model={model or '?'})")

    async def send_status(status: str, **kwargs):
        if not await session.send_json({"type": status, "job_uuid": job_uuid, **kwargs}):
            raise ServerDisconnected()

    await send_status("job_started")
    if state is not None:
        state["job_started"] = True

    graph, ref_err = await apply_ref_images_to_graph(graph, ref_urls, args)
    if ref_err:
        log.error(f"Job {job_uuid}: {ref_err}")
        await send_status("job_failed", reason=ref_err)
        return

    model_lower = (model or "").strip().lower()
    hn_lower = (getattr(args, "hostname", "") or os.environ.get("LOBO_HOSTNAME", "")).lower()
    use_native_ltx = (
        _is_native_executor()
        or hn_lower.startswith("loboforge-ltx")
    ) and (model_lower.startswith("ltx23") or graph_has_ltx_nodes(graph))
    if use_native_ltx:
        await _handle_native_ltx_job(session, job_uuid, graph, send_status, state)
        return

    if model_lower.startswith("ltx23") or graph_has_ltx_nodes(graph):
        inv = (state or {}).get("models") if state else None
        if not inv:
            inv = await get_available_models(args.comfyui_http)
            if state is not None:
                state["models"] = inv
        graph = patch_ltx_graph_models(graph, inv)
    else:
        # Queue-mode specs carry canonical model names (node-agnostic dispatch);
        # remap loaders to this box's own inventory (subfolder layouts differ per box).
        inv = (state or {}).get("models") if state else None
        if not inv:
            inv = await get_available_models(args.comfyui_http)
            if state is not None:
                state["models"] = inv
        graph = patch_graph_model_names(graph, inv)

    te_err = _graph_unresolved_clip_loaders(graph, inv)
    if te_err:
        log.warning("Job %s: %s", job_uuid, te_err)
        await send_status("job_failed", reason=te_err)
        return

    last_pct = [-1]
    last_step_total = [0, 1]

    async def on_progress(step, total):
        pct = int(step / total * 100) if total > 0 else 0
        last_step_total[0] = step
        last_step_total[1] = max(total, 1)
        if pct != last_pct[0]:
            last_pct[0] = pct
            try:
                await send_status("job_progress", step=step, total=total, pct=pct)
            except Exception:
                pass

    async def on_activity():
        if last_pct[0] < 0:
            return
        try:
            await send_status(
                "job_progress",
                step=last_step_total[0],
                total=last_step_total[1],
                pct=last_pct[0],
            )
        except Exception:
            pass

    file_info = await submit_and_wait(
        graph, args.comfyui_http, on_progress, max_total_sec=wall_sec,
        on_activity=on_activity,
    )

    if not file_info:
        # Pull the most specific reason we captured: submit-time HTTP body,
        # or execution_error message keyed by our last prompt_id, or fall
        # back to the generic string. Pop on read so the dict can't grow.
        reason = (_comfy_last_error.pop("__submit__", None)
                  or _comfy_last_error.pop(_comfy_last_error_last_prompt, None)
                  or "ComfyUI did not produce output")
        log.error(f"Job {job_uuid}: no file_info returned — reason: {reason}")
        await send_status("job_failed", reason=reason)
        return

    log.info(f"Job {job_uuid}: got file_info: {file_info}")

    # Download output file
    file_data = await download_output(file_info, args.comfyui_http)
    if not file_data:
        log.error(f"Job {job_uuid}: download_output returned None for {file_info}")
        await send_status("job_failed", reason="Could not download output from ComfyUI")
        return

    filename  = file_info.get("filename", f"{job_uuid}.bin")
    mime_type = guess_mime(filename)
    log.info(f"Job {job_uuid}: downloaded {len(file_data):,} bytes as {filename} ({mime_type})")

    # Transfer file to server via chunked WebSocket binary frames
    log.info(f"Job {job_uuid}: starting file transfer to server...")
    ok = await send_file(session, job_uuid, filename, file_data, mime_type)
    if not ok:
        log.error(f"Job {job_uuid}: send_file failed")
        await send_status("job_failed", reason="File transfer to server failed")
        return

    log.info(f"Job {job_uuid} complete")


# ── Model download handler ────────────────────────────────────────────────

async def handle_tag_job(ws, msg: dict) -> None:
    """
    Handle an assign_tag_job from the server.

    Message:
      { "type": "assign_tag_job", "post_uuid": "...", "image_url": "https://..." }

    Downloads the image over HTTP, runs WD14, and replies with:
      { "type": "tag_job_complete", "post_uuid": "...", "tags": [...], "rating": "...",
        "is_nsfw": bool, "error": null|str }
    """
    post_uuid = msg.get("post_uuid") or ""
    image_url = msg.get("image_url") or ""
    if not post_uuid or not image_url:
        return

    reply = {"type": "tag_job_complete", "post_uuid": post_uuid}

    if not WD14_AVAILABLE or wd14_tagger is None:
        reply["error"] = "wd14 not available on this agent"
        await ws.send(json.dumps(reply))
        return

    try:
        # Fetch the image (server-hosted, reasonable size)
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=60)) as session:
            async with session.get(image_url) as resp:
                if resp.status != 200:
                    reply["error"] = f"fetch failed: HTTP {resp.status}"
                    await ws.send(json.dumps(reply))
                    return
                image_bytes = await resp.read()

        # Tag in worker thread so the event loop stays responsive
        loop = asyncio.get_event_loop()
        result = await loop.run_in_executor(None, wd14_tagger.tag_image_bytes, image_bytes)

        if not isinstance(result, dict) or "error" in result:
            reply["error"] = (result or {}).get("error", "wd14 failed")
            await ws.send(json.dumps(reply))
            return

        reply["tags"]    = result.get("tags", [])
        reply["rating"]  = result.get("rating", "")
        reply["is_nsfw"] = bool(result.get("is_nsfw", False))
        log.info(f"Tag job complete for post={post_uuid}: rating={reply['rating']} tags={len(reply['tags'])}")
    except Exception as e:
        reply["error"] = f"{type(e).__name__}: {e}"
        log.warning(f"Tag job failed for post={post_uuid}: {e}")

    try:
        await ws.send(json.dumps(reply))
    except Exception as e:
        log.warning(f"Failed to send tag_job_complete: {e}")


async def handle_download_model(ws, msg: dict, args) -> None:
    """
    Download a model from a URL and save it to the ComfyUI models folder.
    Message fields:
      - download_id: unique ID for this download request
      - url: download URL (HuggingFace, civitai, direct link)
      - dest_path: relative path within ComfyUI models dir
                   e.g. "loras/my_lora.safetensors"
                   e.g. "diffusion_models/Wan2.2/model.safetensors"
      - model_type: unet | checkpoint | lora | vae | clip (for folder resolution)
    """
    download_id = msg.get("download_id", str(uuid.uuid4()))
    url         = msg.get("url", "")
    dest_path   = msg.get("dest_path", "")
    model_type  = msg.get("model_type", "")

    if not url or not dest_path:
        await ws.send(json.dumps({
            "type": "download_error",
            "download_id": download_id,
            "reason": "Missing url or dest_path"
        }))
        return

    # Resolve absolute path — models live under {comfyui-dir}/models/.
    # The vast.ai/comfy:v0.15.1 image uses LOWERCASE /workspace/comfyui;
    # older or hand-rolled boxes may use uppercase /workspace/ComfyUI. Check
    # both cases (lowercase first, since that's the current default), or the
    # downloaded LoRA lands in `./models/` next to the agent script and
    # ComfyUI never picks it up — which is exactly the silent failure we
    # were hitting before this fix.
    possible_roots = []
    if hasattr(args, "comfyui_models") and args.comfyui_models:
        possible_roots.append(Path(args.comfyui_models))
    try:
        from loboforge_worker.paths import find_models_root
        worker_root = find_models_root(args)
        if worker_root:
            possible_roots.append(worker_root)
    except ImportError:
        pass
    possible_roots.extend([
        Path("/opt/workspace-internal/ComfyUI/models"),  # vast.ai ComfyUI image (supervisord)
        Path("/workspace/comfyui/models"),       # vast.ai default (LOWERCASE)
        Path("/workspace/ComfyUI/models"),       # legacy / hand-rolled
        Path("/root/comfyui/models"),
        Path("/root/ComfyUI/models"),
        Path.home() / "comfyui" / "models",
        Path.home() / "ComfyUI" / "models",
        Path.cwd() / "models",
    ])
    models_root = next((p for p in possible_roots if p and p.exists()), None)
    if models_root is None:
        # Don't write to a phantom path — log + report back so the admin
        # panel surfaces the failure instead of silently dropping the file.
        reason = "Cannot locate ComfyUI models dir on this node (tried: " + \
                 ", ".join(str(p) for p in possible_roots if p) + ")"
        log.error(reason)
        await ws.send(json.dumps({
            "type":        "download_error",
            "download_id": download_id,
            "reason":      reason,
        }))
        return

    abs_path = models_root / dest_path
    abs_path.parent.mkdir(parents=True, exist_ok=True)

    # Keep LoRAs on disk — re-downloading wastes S3 egress. Skip when present.
    min_keep_bytes = 1024 * 1024
    if model_type == "lora" and abs_path.exists():
        try:
            size = abs_path.stat().st_size
        except OSError:
            size = 0
        if size >= min_keep_bytes:
            log.info(f"LoRA already on disk ({size:,} bytes), skipping download: {abs_path}")
            updated_models = await get_available_models(args.comfyui_http)
            await ws.send(json.dumps({
                "type":        "download_complete",
                "download_id": download_id,
                "dest_path":   dest_path,
                "filename":    abs_path.name,
                "models":      updated_models,
            }))
            return

    log.info(f"Downloading model: {url} → {abs_path}")

    await ws.send(json.dumps({
        "type":        "download_progress",
        "download_id": download_id,
        "status":      "starting",
        "filename":    abs_path.name,
        "pct":         0
    }))

    # ── Google Drive routing ────────────────────────────────────────────
    # Drive serves a virus-warning HTML interstitial for files over ~100 MB
    # which aiohttp would silently save AS the model (corrupt) — every
    # wan2.2 LoRA in the seed collection is in that bucket. `gdown` handles
    # the cookie / token dance correctly. No mid-download progress (gdown
    # doesn't expose a callback), but we send a "downloading" frame at the
    # start and "complete"/"error" at the end so the admin UI doesn't
    # freeze with a stale pct.
    if "drive.google.com" in url or "docs.google.com" in url:
        try:
            import gdown
        except ImportError:
            await ws.send(json.dumps({
                "type":        "download_error",
                "download_id": download_id,
                "reason":      "gdown not installed on this agent — `pip install gdown` and retry",
            }))
            return

        await ws.send(json.dumps({
            "type":        "download_progress",
            "download_id": download_id,
            "status":      "downloading",
            "pct":         5,  # arbitrary non-zero so UI shows movement
            "filename":    abs_path.name,
        }))

        def _run_gdown():
            # fuzzy=True lets gdown accept bare IDs, share-links, or the
            # uc?id= form interchangeably — added in gdown 4.5+. Older
            # installs raise TypeError; fall back to the plain call which
            # works for the direct uc?id= URLs the seed builds anyway.
            # quiet=True suppresses tqdm (we don't capture stdout). Returns
            # the dest path on success, None on failure.
            try:
                return gdown.download(url, output=str(abs_path), quiet=True, fuzzy=True)
            except TypeError as te:
                if "fuzzy" in str(te):
                    log.info("gdown lacks 'fuzzy' kwarg — falling back to plain download()")
                    return gdown.download(url, output=str(abs_path), quiet=True)
                raise

        try:
            loop   = asyncio.get_running_loop()
            result = await loop.run_in_executor(None, _run_gdown)
            if not result or not abs_path.exists() or abs_path.stat().st_size < 1024:
                # gdown sometimes leaves a stub HTML page on disk if the
                # file is shared restricted-access; treat tiny files as
                # failure and clean up.
                try: abs_path.unlink(missing_ok=True)
                except Exception: pass
                raise Exception("gdown returned no usable file (private/restricted?)")

            log.info(f"gdown download complete: {abs_path} ({abs_path.stat().st_size} bytes)")
            updated_models = await get_available_models(args.comfyui_http)
            await ws.send(json.dumps({
                "type":        "download_complete",
                "download_id": download_id,
                "dest_path":   dest_path,
                "filename":    abs_path.name,
                "models":      updated_models,
            }))
        except Exception as e:
            log.error(f"gdown failed: {e}")
            try:
                if abs_path.exists(): abs_path.unlink()
            except Exception: pass
            await ws.send(json.dumps({
                "type":        "download_error",
                "download_id": download_id,
                "reason":      f"gdown: {e}",
            }))
        return

    # ── HuggingFace / direct HTTP path (legacy, unchanged) ─────────────
    try:
        async with aiohttp.ClientSession() as session:
            # Support HuggingFace tokens via URL or header
            headers = {}
            if "huggingface.co" in url and hasattr(args, 'hf_token') and args.hf_token:
                headers["Authorization"] = f"Bearer {args.hf_token}"

            async with session.get(url, headers=headers,
                                    timeout=aiohttp.ClientTimeout(total=3600),
                                    allow_redirects=True) as r:
                if not r.ok:
                    raise Exception(f"HTTP {r.status}: {await r.text()}")

                total_size = int(r.headers.get("Content-Length", 0))
                downloaded = 0
                last_pct   = -1

                with open(abs_path, "wb") as f:
                    async for chunk in r.content.iter_chunked(1024 * 1024):  # 1MB chunks
                        f.write(chunk)
                        downloaded += len(chunk)
                        if total_size > 0:
                            pct = int(downloaded / total_size * 100)
                            if pct != last_pct and pct % 5 == 0:  # report every 5%
                                last_pct = pct
                                try:
                                    await ws.send(json.dumps({
                                        "type":        "download_progress",
                                        "download_id": download_id,
                                        "status":      "downloading",
                                        "pct":         pct,
                                        "downloaded":  downloaded,
                                        "total":       total_size,
                                        "filename":    abs_path.name
                                    }))
                                except Exception:
                                    pass

        # Refresh model list and send back to server
        log.info(f"Download complete: {abs_path}")
        updated_models = await get_available_models(args.comfyui_http)

        await ws.send(json.dumps({
            "type":        "download_complete",
            "download_id": download_id,
            "dest_path":   dest_path,
            "filename":    abs_path.name,
            "models":      updated_models  # refreshed model list
        }))

    except Exception as e:
        log.error(f"Download failed: {e}")
        # Remove partial file
        try:
            if abs_path.exists():
                abs_path.unlink()
        except Exception:
            pass
        await ws.send(json.dumps({
            "type":        "download_error",
            "download_id": download_id,
            "reason":      str(e)
        }))


# ── Pull-based work loop ───────────────────────────────────────────────────────

async def accept_assign_job(session: ServerWsSession, msg: dict, args, state: dict) -> None:
    """Run a server-pushed or pull-response assign_job (canaries, admin tests, queue work)."""
    global _active_job_task, _active_job_uuid
    incoming = msg.get("job_uuid")
    if state.get("comfy_external_busy") and state.get("current_job") is None:
        log.warning("Rejecting assign_job — ComfyUI busy with private/local work")
        try:
            await session.send_json({
                "type":     "job_failed",
                "job_uuid": incoming,
                "reason":   "Node is busy",
            })
        except Exception:
            pass
        return
    if _active_job_task is not None and not _active_job_task.done():
        if _active_job_uuid == incoming or state.get("current_job") == incoming:
            log.debug(f"Duplicate assign_job for {incoming} — ignoring")
            return
        log.warning("Got assign_job while busy — rejecting")
        try:
            await session.send_json({
                "type":     "job_failed",
                "job_uuid": msg.get("job_uuid"),
                "reason":   "Node is busy",
            })
        except Exception:
            pass
        return
    if state.get("current_job") == incoming:
        log.debug(f"Duplicate assign_job for {incoming} — ignoring")
        return
    if state.get("current_job") is not None:
        log.warning("Got assign_job while busy — rejecting")
        try:
            await session.send_json({
                "type":     "job_failed",
                "job_uuid": msg.get("job_uuid"),
                "reason":   "Node is busy",
            })
        except Exception:
            pass
        return
    log.info(f"Job {incoming} received")
    state["current_job"] = incoming
    state["current_job_since"] = time.time()
    state["job_started"] = False
    _active_job_uuid = incoming
    _active_job_task = asyncio.create_task(run_pulled_job(session, msg, args, state))


async def lora_prefetch_loop(session: ServerWsSession, args, state: dict) -> None:
    """LoRA prefetch when idle — server returns download_lora or empty only (jobs are SQS-only)."""
    empty_backoff = 2.0
    while True:
        if agent_fleet_busy(state):
            await asyncio.sleep(1.0)
            continue
        if session.waiting:
            await asyncio.sleep(0.5)
            continue
        try:
            await session.send_json({
                "type":        "request_work",
                "node_uuid":   args.node_uuid,
                "hostname":    args.hostname,
                "vram_free":   get_vram_free(),
                "disk_free_mb": get_disk_free_mb(),
                "known_loras": state.get("known_loras", []),
            })
            msg = await session.wait_response({"download_lora", "empty"}, 120.0)
        except asyncio.TimeoutError:
            continue
        except Exception as e:
            log.warning(f"request_work failed: {e}")
            await asyncio.sleep(empty_backoff)
            empty_backoff = min(empty_backoff * 1.5, 15.0)
            continue

        empty_backoff = 2.0
        mtype = msg.get("type", "")

        if mtype == "empty":
            await asyncio.sleep(empty_backoff)
            continue

        if mtype == "download_lora":
            await handle_download_model(session.ws, msg, args)
            models = await get_available_models(args.comfyui_http)
            state["models"] = models
            basename = msg.get("lora_basename") or Path(msg.get("dest_path", "")).name
            if basename:
                known = state.setdefault("known_loras", [])
                if basename not in known:
                    known.append(basename)
            continue

        if mtype == "assign_job":
            log.error("request-work returned assign_job — ignored (jobs are SQS-only)")


async def run_pulled_job(session: ServerWsSession, job_msg: dict, args, state: dict) -> None:
    global _active_job_task, _active_job_uuid
    job_id = job_msg.get("job_uuid")
    try:
        await handle_job(session, job_msg, args, state)
    except ServerDisconnected:
        log.warning(
            "Job %s: server disconnected before status could be sent — "
            "Comfy may still be running until reconnect",
            job_id,
        )
    except asyncio.CancelledError:
        log.info("Job %s cancelled", job_id)
        raise
    except Exception as e:
        log.exception(f"Unhandled error in job {job_id}")
        await session.send_json({
            "type":     "job_failed",
            "job_uuid": job_id,
            "reason":   str(e),
        })
    finally:
        if _active_job_task is asyncio.current_task():
            _active_job_task = None
            _active_job_uuid = None
        state["current_job"] = None
        state.pop("current_job_since", None)
        state.pop("job_started", None)


async def handle_legacy_worker_command(session, msg: dict, args) -> bool:
    """Handle restart/reprovision on boxes with stale loboforge_worker (pre-registry commands)."""
    cmd = (msg.get("command") or "").strip()
    if cmd not in ("restart_agent", "reprovision"):
        return False

    cid = msg.get("command_id") or msg.get("id") or "?"
    base = getattr(args, "server", "wss://www.loboforge.com")
    if base.startswith("ws"):
        base = "https://" + base.split("://", 1)[1]
    base = base.replace("wss://", "https://").replace("ws://", "http://").rstrip("/")
    agent_dir = Path(getattr(args, "agent_dir", "/workspace"))

    session_name = str(getattr(args, "tmux_session", None) or "loboforge-agent")

    def _refresh_only() -> None:
        import urllib.request

        urllib.request.urlretrieve(f"{base}/agent/loboforge_agent.py", str(agent_dir / "loboforge_agent.py"))
        tar = agent_dir / ".loboforge_worker_refresh.tar.gz"
        urllib.request.urlretrieve(f"{base}/agent/loboforge_worker.tar.gz", tar)
        subprocess.run(["tar", "-xzf", str(tar), "-C", str(agent_dir)], check=False, capture_output=True)
        tar.unlink(missing_ok=True)
        if cmd == "reprovision":
            for name in (".loboforge-provision-done", ".loboforge-artifact-skips.json"):
                (agent_dir / name).unlink(missing_ok=True)

    def _kill_agent_session() -> None:
        subprocess.run(
            ["tmux", "kill-session", "-t", session_name],
            check=False,
            capture_output=True,
        )

    try:
        await asyncio.to_thread(_refresh_only)
        await session.send_json({
            "type": "command_result",
            "command_id": cid,
            "ok": True,
            "result": {"restarting": True, "legacy": True, "command": cmd, "session": session_name},
            "error": "",
        })

        async def _delayed_kill() -> None:
            await asyncio.sleep(1.5)
            await asyncio.to_thread(_kill_agent_session)

        asyncio.create_task(_delayed_kill())
    except Exception as ex:
        await session.send_json({
            "type": "command_result",
            "command_id": cid,
            "ok": False,
            "result": {},
            "error": str(ex),
        })
    return True


async def ensure_box_ready(args, *, max_wait_sec: int = 900) -> bool:
    """Join the fleet pool immediately; background provision continues after connect."""
    hn = (getattr(args, "hostname", "") or "").lower()
    if hn.startswith("local-") or getattr(args, "skip_canary", False):
        http = getattr(args, "comfyui_http", "http://127.0.0.1:8188")
        if await comfyui_is_healthy(http):
            try:
                setattr(args, "_hello_models", await get_available_models(http))
            except Exception:
                pass
        else:
            log.warning("Local dev: ComfyUI not responding on %s — connecting anyway", http)
        return True

    try:
        from loboforge_worker.provision.ready import prepare_for_connect
    except ImportError:
        if not await ensure_p100_pytorch(args):
            return False
        await ensure_comfyui_running(args)
        return True

    mode = os.environ.get("LOBO_MODE", os.environ.get("MODE", "all"))
    if "image" in hn:
        mode = "image"
    elif "video" in hn:
        mode = "video"
    elif hn.startswith("loboforge-ltx") or _is_native_executor():
        mode = "ltx-native"

    result = await prepare_for_connect(args, mode=mode)
    if not result.get("ok"):
        log.error("Connect prep failed: %s", result.get("error"))
        return False
    models = result.get("models") or {}
    if models:
        setattr(args, "_hello_models", models)
    if result.get("hostname"):
        args.hostname = result["hostname"]
    return True

async def run_agent(args, *, enable_worker: bool = True) -> None:
    """Main loop — connects to server and handles messages. Reconnects aggressively."""
    gpu_info = get_gpu_info()
    log.info(f"GPU: {gpu_info['gpu_name']} | VRAM: {gpu_info['vram_total']} MB")

    # Build the WebSocket URL
    ws_url = f"{args.server.rstrip('/')}/ws/gpu-agent"
    if not ws_url.startswith("ws"):
        ws_url = "wss://" + ws_url

    reconnect_delay = RECONNECT_MIN

    while True:
        if not await ensure_box_ready(args):
            log.error("Box connect prep failed — retrying in 30s")
            await asyncio.sleep(30)
            continue

        try:
            log.info(f"Connecting to {ws_url} ...")

            headers = {"X-Agent-Secret": args.secret}

            async with websockets.connect(
                ws_url,
                additional_headers=headers,
                ping_interval=WS_PING_INTERVAL,
                ping_timeout=WS_PING_TIMEOUT,
                open_timeout=60,
                close_timeout=10,
                max_size=32 * 1024 * 1024,  # 32 MB frames for chunked uploads
            ) as ws:
                log.info("Connected to LoboForge server")
                reconnect_delay = RECONNECT_MIN

                # Set ComfyUI auth token globally
                global _comfy_token
                _comfy_token = args.comfyui_token

                # Comfy verified in prepare_for_connect; may still be warming during provision.

                # Start (or restart) the persistent ComfyUI WS reader.
                # Cancel any leftover reader from the previous connection so we
                # don't accumulate multiple readers racing on _comfy_listeners.
                global _comfy_reader_task
                if _comfy_reader_task is not None and not _comfy_reader_task.done():
                    _comfy_reader_task.cancel()
                    try:
                        await _comfy_reader_task
                    except (asyncio.CancelledError, Exception):
                        pass
                if not _is_skip_comfy():
                    _comfy_reader_task = asyncio.create_task(comfy_ws_reader(args.comfyui_ws))

                # Send hello — retry model query if ComfyUI is still loading models
                models = getattr(args, "_hello_models", None) or await load_models_with_retry(args)
                if hasattr(args, "_hello_models"):
                    delattr(args, "_hello_models")
                known_loras = list(models.get("loras", []))
                hn = (getattr(args, "hostname", "") or "").lower()
                block_fleet_comfy = getattr(args, "block_fleet_when_comfy_busy", None)
                if block_fleet_comfy is None:
                    block_fleet_comfy = hn.startswith("local-")
                agent_state = {
                    "current_job": None,
                    "known_loras": known_loras,
                    "models": models,
                    "provisioning": bool(enable_worker),
                    "provision_step": "starting" if enable_worker else "",
                    "provision_pct": 0 if enable_worker else 100,
                    "block_fleet_when_comfy_busy": bool(block_fleet_comfy),
                    "comfy_external_busy": False,
                }
                if block_fleet_comfy:
                    log.info(
                        "Comfy private-busy guard on — node reports busy while ComfyUI "
                        "runs untracked prompts"
                    )
                if not _is_skip_comfy():
                    await refresh_comfy_external_busy(args, agent_state)
                comfy_ok = True if _is_skip_comfy() else await comfyui_is_healthy(args.comfyui_http)
                caps = {"wd14": WD14_AVAILABLE, "joycaption": JOYCAPTION_AVAILABLE}
                if _is_native_executor():
                    from loboforge_worker.capabilities import native_executor_caps
                    caps.update(native_executor_caps())
                await ws.send(json.dumps({
                    "type":         "hello",
                    "node_uuid":    args.node_uuid,
                    "hostname":     args.hostname,
                    "gpu_name":     gpu_info["gpu_name"],
                    "vram_total":   gpu_info["vram_total"],
                    "vram_free":    get_vram_free(),
                    "disk_free_mb": get_disk_free_mb(),
                    "models":       models,
                    "comfyui_url":  args.comfyui_http,
                    "comfy_ok":     comfy_ok,
                    "provisioning": agent_state["provisioning"],
                    "provision_step": agent_state.get("provision_step", ""),
                    "provision_pct": agent_state.get("provision_pct", 0),
                    "provision_mode": resolve_fleet_mode(args),
                    "capabilities": caps,
                    **({"skip_canary": True} if getattr(args, "skip_canary", False) else {}),
                }))
                log.info(f"Sent hello (wd14={'on' if WD14_AVAILABLE else 'off'}, models={model_count(models)})")

                session = ServerWsSession(ws)

                worker_tasks: list = []
                _handle_command = None
                worker_pull_loop_active = False
                if enable_worker:
                    try:
                        from loboforge_worker.integration import start_worker_runtime, handle_command_message
                        worker_tasks = start_worker_runtime(session, args, agent_state)
                        _handle_command = handle_command_message
                        worker_pull_loop_active = True
                        log.info("loboforge_worker runtime active (%d tasks)", len(worker_tasks))
                    except ImportError as ex:
                        log.warning("loboforge_worker unavailable (%s) — LoRA prefetch loop", ex)
                        worker_tasks.append(asyncio.create_task(lora_prefetch_loop(session, args, agent_state)))
                        worker_tasks.append(asyncio.create_task(comfy_watchdog(args, agent_state, session)))
                else:
                    log.info("LoRA prefetch loop only (worker runtime disabled)")
                    worker_tasks.append(asyncio.create_task(lora_prefetch_loop(session, args, agent_state)))
                    worker_tasks.append(asyncio.create_task(comfy_watchdog(args, agent_state, session)))

                # Heartbeat — keeps connection alive through proxies during 5–10 min ComfyUI runs
                async def heartbeat():
                    while True:
                        await asyncio.sleep(HEARTBEAT_INTERVAL)
                        try:
                            if not _is_skip_comfy():
                                await refresh_comfy_external_busy(args, agent_state)
                            hb_comfy_ok = True if _is_skip_comfy() else await comfyui_is_healthy(args.comfyui_http)
                            await session.send_json({
                                "type":             "heartbeat",
                                "node_uuid":        args.node_uuid,
                                "vram_free":        get_vram_free(),
                                "disk_free_mb":     get_disk_free_mb(),
                                "busy":             agent_fleet_busy(agent_state),
                                "current_job_uuid": agent_state["current_job"],
                                "comfy_ok":         hb_comfy_ok,
                                "model_count":      model_count(agent_state.get("models", {})),
                                "provisioning":     bool(agent_state.get("provisioning")),
                                "provision_step":   agent_state.get("provision_step", ""),
                                "provision_pct":    int(agent_state.get("provision_pct") or 0),
                            })
                        except Exception:
                            break

                hb_task = asyncio.create_task(heartbeat())

                try:
                    async for raw in ws:
                        if isinstance(raw, bytes):
                            continue

                        try:
                            msg = json.loads(raw)
                        except json.JSONDecodeError:
                            continue

                        if session.try_deliver(msg):
                            continue

                        mtype = msg.get("type", "")
                        log.debug(f"← {mtype}")

                        if mtype == "assign_job":
                            log.error("Unsolicited assign_job over WS — ignored (jobs are SQS-only)")

                        elif mtype == "release_job":
                            released = msg.get("job_uuid")
                            await cancel_active_job(
                                "release_job from server",
                                job_uuid=released or None,
                            )
                            if agent_state.get("current_job") in (None, released):
                                agent_state["current_job"] = None
                                agent_state.pop("current_job_since", None)
                                agent_state.pop("job_started", None)
                                log.info(f"Released stale job hold{f' ({released[:8]}…)' if released else ''}")

                        elif mtype == "cancel_job":
                            log.warning(f"Cancel requested for {msg.get('job_uuid')} — not yet implemented")

                        elif mtype == "download_model":
                            asyncio.create_task(handle_download_model(ws, msg, args))

                        elif mtype == "download_lora" and not worker_pull_loop_active:
                            # Hello-time prefetch can arrive before pull loop is waiting.
                            async def _prefetch_lora():
                                await handle_download_model(ws, msg, args)
                                models = await get_available_models(args.comfyui_http)
                                agent_state["models"] = models
                                basename = msg.get("lora_basename") or Path(msg.get("dest_path", "")).name
                                if basename:
                                    known = agent_state.setdefault("known_loras", [])
                                    if basename not in known:
                                        known.append(basename)
                            asyncio.create_task(_prefetch_lora())

                        elif mtype == "assign_tag_job":
                            asyncio.create_task(handle_tag_job(ws, msg))

                        elif mtype == "ping":
                            await session.send_json({"type": "pong"})

                        elif mtype == "command":
                            if _handle_command:
                                await _handle_command(msg, session, args, agent_state)
                            else:
                                await handle_legacy_worker_command(session, msg, args)

                        elif mtype == "worker_event":
                            pass  # server-origin only; workers send, not receive

                finally:
                    await cancel_active_job("server disconnect")
                    hb_task.cancel()
                    for task in worker_tasks:
                        task.cancel()
                    for task in [hb_task, *worker_tasks]:
                        try:
                            await task
                        except asyncio.CancelledError:
                            pass

        except (websockets.exceptions.ConnectionClosed,
                websockets.exceptions.WebSocketException,
                OSError,
                asyncio.TimeoutError) as e:
            log.warning(f"Connection lost: {e}")
        except Exception as e:
            log.error(f"Unexpected error: {e}")

        log.info(f"Reconnecting in {reconnect_delay}s...")
        await asyncio.sleep(reconnect_delay)
        reconnect_delay = min(reconnect_delay * 1.5, RECONNECT_MAX)


# ── Entry point ────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="LoboForge GPU Agent")
    parser.add_argument("--server",       default=DEFAULT_SERVER,
                        help="LoboForge server WebSocket URL")
    parser.add_argument("--secret",       required=True,
                        help="Node secret (set in admin panel)")
    parser.add_argument("--node-uuid",    default=None,
                        help="Node UUID (auto-generated and saved if not provided)")
    parser.add_argument("--hostname",     default=os.uname().nodename,
                        help="Human-readable node name")
    parser.add_argument("--comfyui-http", default=DEFAULT_COMFYUI_HTTP,
                        help="ComfyUI HTTP base URL")
    parser.add_argument("--comfyui-ws",   default=DEFAULT_COMFYUI_WS,
                        help="ComfyUI WebSocket base URL")
    parser.add_argument("--comfyui-models", default=None,
                        help="Path to ComfyUI models directory (auto-detected if not set)")
    parser.add_argument("--comfyui-dir", default=None,
                        help="Path to ComfyUI install (for self-healing restart)")
    parser.add_argument("--comfyui-token",  default=None,
                        help="ComfyUI auth token (if instance requires authentication)")
    parser.add_argument("--hf-token",      default=None,
                        help="HuggingFace token for gated model downloads")
    parser.add_argument("--debug",        action="store_true",
                        help="Enable debug logging")
    parser.add_argument(
        "--block-fleet-when-comfy-busy",
        action=argparse.BooleanOptionalAction,
        default=None,
        help="Report busy / skip pull while ComfyUI runs untracked (private) prompts "
             "(default: on for local-* hostnames)",
    )
    args = parser.parse_args()
    if args.block_fleet_when_comfy_busy is not None:
        args.block_fleet_when_comfy_busy = bool(args.block_fleet_when_comfy_busy)

    if args.debug:
        logging.getLogger().setLevel(logging.DEBUG)

    # Persist node UUID so it survives restarts
    uuid_file = Path("~/.loboforge_node_uuid").expanduser()
    if args.node_uuid:
        uuid_file.write_text(args.node_uuid)
    elif uuid_file.exists():
        args.node_uuid = uuid_file.read_text().strip()
    else:
        args.node_uuid = str(uuid.uuid4())
        uuid_file.write_text(args.node_uuid)
        log.info(f"Generated node UUID: {args.node_uuid}")

    log.info(f"LoboForge GPU Agent starting")
    log.info(f"  Node UUID:   {args.node_uuid}")
    log.info(f"  Hostname:    {args.hostname}")
    log.info(f"  Server:      {args.server}")
    log.info(f"  ComfyUI:     {args.comfyui_http}")
    if WD14_AVAILABLE:
        log.info("  WD14 tagger: available — preloading model in background")
        # Warm the session in a thread so first real job doesn't pay the cold start
        import threading
        threading.Thread(target=wd14_tagger.preload, daemon=True).start()
    else:
        log.info(f"  WD14 tagger: unavailable ({_WD14_IMPORT_ERROR}). "
                 f"Install: pip install onnxruntime-gpu Pillow numpy")

    asyncio.run(run_agent(args))


if __name__ == "__main__":
    main()
