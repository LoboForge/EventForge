"""Shared GPU worker helpers (check-in, LoRA prefetch) — no MQTT/IoT."""

from __future__ import annotations

import argparse
import os
import logging
from pathlib import Path
from typing import Any

import loboforge_agent as agent

log = logging.getLogger("gpu-agent-common")

CHECK_IN_INTERVAL = 60


def _load_persisted_env() -> None:
    try:
        from loboforge_worker.env_loader import ensure_loboforge_env
        ensure_loboforge_env()
    except ImportError:
        pass


def probe_event_forge_access(capabilities: list[str] | None = None) -> dict[str, Any]:
    """EventForge reachability for admin queue badges (replaces SQS probe)."""
    _load_persisted_env()
    import urllib.error
    import urllib.request

    base = (
        os.environ.get("EVENT_FORGE_URL")
        or os.environ.get("EVENT_FORGE_WORKER_URL")
        or ""
    ).strip().rstrip("/")
    worker_key = (os.environ.get("EVENT_FORGE_WORKER_KEY") or "").strip()
    out: dict[str, Any] = {"queue_access_ok": False}

    if not base:
        out["queue_access_error"] = "EVENT_FORGE_URL not set"
        return out
    if not worker_key:
        out["queue_access_error"] = "EVENT_FORGE_WORKER_KEY not set"
        return out

    def _get(path: str, auth: bool = False) -> tuple[int, str]:
        req = urllib.request.Request(f"{base}{path}")
        # Cloudflare blocks urllib's default Python-urllib/* UA (403) on eventforge.loboforge.com.
        req.add_header("User-Agent", "LoboForge-Worker/1.1")
        if auth:
            req.add_header("Authorization", f"Bearer {worker_key}")
        try:
            with urllib.request.urlopen(req, timeout=8) as resp:
                return resp.status, resp.read(256).decode("utf-8", errors="replace")
        except urllib.error.HTTPError as ex:
            body = ex.read(256).decode("utf-8", errors="replace") if ex.fp else ""
            return ex.code, body
        except Exception as ex:
            return 0, f"{type(ex).__name__}: {ex}"[:120]

    health_code, _ = _get("/health")
    if health_code != 200:
        out["queue_access_error"] = f"EventForge /health HTTP {health_code or 'error'}"[:120]
        return out
    ws_code, _ = _get("/healthws")
    if ws_code != 200:
        out["queue_access_error"] = f"EventForge /healthws HTTP {ws_code or 'error'}"[:120]
        return out

    stats_code, stats_body = _get("/v1/queue/stats", auth=True)
    if stats_code == 401:
        out["queue_access_error"] = "EventForge worker key rejected (401)"
        return out
    if stats_code != 200:
        out["queue_access_error"] = f"EventForge /v1/queue/stats HTTP {stats_code}"[:120]
        return out

    caps = [c.strip().lower() for c in (capabilities or []) if isinstance(c, str) and c.strip()]
    if caps:
        out["event_forge_capabilities"] = caps
    out["queue_access_ok"] = True
    out["event_forge_url"] = base
    if stats_body:
        out["event_forge_stats_preview"] = stats_body[:80]
    return out


def probe_forge_queue_access(capabilities: list[str]) -> dict[str, Any]:
    """Legacy name — EventForge probe only (no AWS/SQS)."""
    return probe_event_forge_access(capabilities)


def resolve_lobo_http_base() -> str:
    raw = (
        os.environ.get("LOBO_BASE_URL")
        or os.environ.get("LOBO_SERVER")
        or "https://www.loboforge.com"
    ).strip().rstrip("/")
    if raw.startswith("wss://"):
        return "https://" + raw[len("wss://") :]
    if raw.startswith("ws://"):
        return "http://" + raw[len("ws://") :]
    if raw.startswith("https://") or raw.startswith("http://"):
        return raw
    return "https://" + raw


def resolve_lora_sync_mode(hostname: str | None = None) -> str:
    """Map box hostname / LOBO_MODE to LoboForge active-loras modes= query."""
    explicit = (os.environ.get("LOBO_LORA_SYNC_MODE") or "").strip().lower()
    if explicit:
        return explicit.split(",")[0]
    mode = (os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()
    if mode:
        norm = mode.split(",")[0]
        if norm in ("all", "both", "ltx-native", "ltx"):
            return "all"
        if norm == "wan-native":
            return "video"
        return norm
    hn = (hostname or "").lower()
    if "-all-" in hn:
        return "all"
    if "-video-" in hn or "-wan-" in hn:
        return "video"
    if "-image-" in hn:
        return "image"
    if "-ltx-" in hn:
        return "all"
    return "all"


def _is_native_ltx_box(hostname: str | None = None) -> bool:
    mode = (os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()
    executor = os.environ.get("LOBO_EXECUTOR", "").strip().lower()
    hn = (hostname or os.environ.get("LOBO_HOSTNAME") or os.environ.get("HN") or "").lower()
    if mode == "wan-native" or "wan-native" in hn or hn.startswith("loboforge-wan-"):
        return False
    return (
        mode == "ltx-native"
        or "-ltx-" in hn
        or hn.startswith("loboforge-ltx")
        or (executor == "native" and _ltx23_enabled())
    )


def _is_native_wan_box(hostname: str | None = None) -> bool:
    mode = (os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()
    executor = os.environ.get("LOBO_EXECUTOR", "").strip().lower()
    hn = (hostname or os.environ.get("LOBO_HOSTNAME") or os.environ.get("HN") or "").lower()
    if _is_native_ltx_box(hostname):
        return False
    return (
        mode == "wan-native"
        or "wan-native" in hn
        or hn.startswith("loboforge-wan-")
        or (executor == "native" and mode in ("wan-native", "wan", ""))
    )


def _native_wan_layout_ready() -> bool:
    """Native Wan boxes must not poll/claim until layout.json exists and I2V weights are on disk."""
    if not _is_native_wan_box():
        return True
    wan_root = (os.environ.get("WAN_MODEL_ROOT") or "/workspace/wan-models").strip()
    try:
        from loboforge_worker.inference.wan.paths import i2v_ready, load_layout, missing_artifacts, wan_model_root

        root = Path(wan_root) if wan_root else wan_model_root()
        return (
            load_layout(root) is not None
            and i2v_ready(root)
            and not missing_artifacts(root)
        )
    except ImportError:
        return Path(wan_root, "layout.json").is_file()


def _native_ltx_layout_ready() -> bool:
    """Native LTX boxes claim only when layout.json + distilled stack + HF Gemma are on disk."""
    if not _is_native_ltx_box():
        return True
    ltx_root = (os.environ.get("LTX_MODEL_ROOT") or "/workspace/ltx-models").strip()
    try:
        from loboforge_worker.inference.ltx.paths import load_layout, missing_artifacts, ltx_model_root

        root = Path(ltx_root) if ltx_root else ltx_model_root()
        return load_layout(root) is not None and not missing_artifacts(root)
    except ImportError:
        return Path(ltx_root, "layout.json").is_file()


def _find_comfy_models_root() -> Path | None:
    """ComfyUI models tree — used for LoRA sync from loboforge.com active-loras."""
    mode = (os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()
    wan_root = os.environ.get("WAN_MODEL_ROOT", "").strip()
    if mode == "wan-native" or (
        os.environ.get("LOBO_EXECUTOR", "").strip().lower() == "native" and wan_root
    ):
        p = Path(wan_root)
        if p.is_dir():
            return p
    for candidate in (
        "/opt/workspace-internal/ComfyUI/models",
        "/workspace/ComfyUI/models",
        "/workspace/comfyui/models",
        "/opt/ComfyUI/models",
        "/ComfyUI/models",
        "/root/ComfyUI/models",
        "/root/comfyui/models",
    ):
        p = Path(candidate)
        if p.is_dir():
            return p
    try:
        from loboforge_worker.paths import find_models_root

        root = find_models_root(argparse.Namespace(hostname=os.environ.get("HOSTNAME", "")))
        if root is not None:
            return root
    except Exception:
        pass
    models_env = os.environ.get("MODELS")
    if models_env:
        mp = Path(models_env)
        if mp.is_dir():
            return mp if mp.name == "models" else mp / "models"
    return None


def _native_wan_lora_dirs() -> list[Path]:
    """Directories where native Wan user LoRAs may live on disk."""
    dirs: list[Path] = []
    wan_root = (os.environ.get("WAN_MODEL_ROOT") or "").strip()
    if wan_root:
        dirs.append(Path(wan_root) / "loras")
    for candidate in (
        "/opt/workspace-internal/ComfyUI/models/loras",
        "/workspace/ComfyUI/models/loras",
    ):
        p = Path(candidate)
        if p.is_dir():
            dirs.append(p)
    return dirs


def sync_hub_active_loras(args: argparse.Namespace, mode: str | None = None) -> dict[str, Any]:
    """
    Pull active LoRAs for this box's mode from LoboForge (GET /api/agent/active-loras).
    Uses loboforge_worker when installed; inline HTTP fallback otherwise.
    """
    sync_mode = mode or resolve_lora_sync_mode(getattr(args, "hostname", None))
    secret = (getattr(args, "secret", None) or os.environ.get("LOBO_SECRET") or "").strip()
    if not secret:
        raise ValueError("LOBO_SECRET not set — cannot sync LoRAs from LoboForge")

    root = _find_comfy_models_root()
    if root is None:
        raise FileNotFoundError("ComfyUI models directory not found")

    base = resolve_lobo_http_base()
    hf_token = os.environ.get("HF_TOKEN") or os.environ.get("HUGGINGFACE_HUB_TOKEN")

    try:
        from loboforge_worker.provision.loras import sync_active_loras

        result = sync_active_loras(
            root, base_url=base, secret=secret, mode=sync_mode, hf_token=hf_token,
        )
        log.info(
            "LoRA sync mode=%s pulled=%s skipped=%s failed=%s",
            sync_mode, result.get("pulled"), result.get("skipped"), result.get("failed"),
        )
        return result
    except ImportError:
        pass

    import json
    import urllib.request

    url = f"{base.rstrip('/')}/api/agent/active-loras?modes={sync_mode}&secret={secret}"
    req = urllib.request.Request(url, headers={"User-Agent": "LoboForge-Worker/1.1"})
    with urllib.request.urlopen(req, timeout=120) as resp:
        items = json.loads(resp.read().decode())
    if not isinstance(items, list):
        items = []

    loras_dir = root / "loras"
    loras_dir.mkdir(parents=True, exist_ok=True)
    pulled = skipped = failed = 0
    min_bytes = 1_000_000
    for row in items:
        if not isinstance(row, dict):
            continue
        fp = (row.get("file_path") or row.get("filePath") or "").strip()
        su = (row.get("source_url") or row.get("sourceUrl") or "").strip()
        if not fp or not su:
            continue
        rel = fp.replace("\\", "/").lstrip("/")
        if rel.lower().startswith("loras/"):
            rel = rel[6:]
        dest = root / "loras" / Path(rel).name
        if dest.is_file() and dest.stat().st_size > min_bytes:
            skipped += 1
            continue
        try:
            if "drive.google.com" in su or "docs.google.com" in su:
                import gdown
                gdown.download(su, str(dest), quiet=True, fuzzy=True)
                if dest.is_file() and dest.stat().st_size > min_bytes:
                    pulled += 1
                    log.info("Pulled LoRA %s (gdown)", dest.name)
                    continue
            dl_req = urllib.request.Request(su, headers={"User-Agent": "LoboForge-Worker/1.1"})
            with urllib.request.urlopen(dl_req, timeout=300) as dl_resp:
                data = dl_resp.read()
            if len(data) < min_bytes:
                failed += 1
                continue
            dest.write_bytes(data)
            pulled += 1
            log.info("Pulled LoRA %s", dest.name)
        except Exception as ex:
            failed += 1
            log.warning("LoRA download failed %s: %s", dest.name, ex)

    result = {"mode": sync_mode, "pulled": pulled, "skipped": skipped, "failed": failed, "source": "active-loras"}
    log.info("LoRA sync (inline) mode=%s pulled=%s skipped=%s failed=%s", sync_mode, pulled, skipped, failed)
    return result



def download_eventforge_job_lora(
    args: argparse.Namespace,
    job_id: str,
    file_name: str,
    *,
    ef_base: str | None = None,
    worker_key: str | None = None,
) -> Path | None:
    """
    Download a ready app LoRA from EventForge for a job into the local models/loras tree.
    Returns dest path on success, None if not found / unavailable.
    """
    base = (ef_base or os.environ.get("EVENT_FORGE_URL") or "").strip().rstrip("/")
    key = (worker_key or os.environ.get("EVENT_FORGE_WORKER_KEY") or "").strip()
    if not base or not key or not job_id or not file_name:
        return None

    root = _find_comfy_models_root()
    if root is None:
        log.warning("EventForge LoRA download skipped — Comfy models root not found")
        return None

    safe = _normalize_lora_basename(file_name)
    if not safe:
        return None
    dest = root / "loras" / safe
    dest.parent.mkdir(parents=True, exist_ok=True)
    min_bytes = 1_000_000
    if dest.is_file() and dest.stat().st_size >= min_bytes:
        return dest

    import urllib.error
    import urllib.parse
    import urllib.request

    url = f"{base}/v1/jobs/{job_id}/loras/{urllib.parse.quote(safe)}"
    req = urllib.request.Request(
        url,
        headers={
            "Authorization": f"Bearer {key}",
            "User-Agent": "LoboForge-Worker/1.1",
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=600) as resp:
            data = resp.read()
    except urllib.error.HTTPError as ex:
        if ex.code == 404:
            return None
        log.warning("EventForge LoRA download HTTP %s for %s: %s", ex.code, safe, ex)
        return None
    except Exception as ex:
        log.warning("EventForge LoRA download failed for %s: %s", safe, ex)
        return None

    if len(data) < min_bytes:
        log.warning("EventForge LoRA %s too small (%d bytes)", safe, len(data))
        return None
    tmp = dest.with_suffix(dest.suffix + ".partial")
    tmp.write_bytes(data)
    tmp.replace(dest)
    log.info("Pulled EventForge LoRA %s (%d bytes) for job %s", safe, len(data), job_id[:8])
    return dest


def pull_missing_loras_from_eventforge(
    args: argparse.Namespace,
    job_id: str,
    missing: list[str],
    *,
    ef_base: str | None = None,
    worker_key: str | None = None,
) -> list[str]:
    """Try to download each missing LoRA from EventForge. Returns basenames successfully pulled."""
    pulled: list[str] = []
    for name in missing:
        path = download_eventforge_job_lora(
            args, job_id, name, ef_base=ef_base, worker_key=worker_key,
        )
        if path is not None:
            pulled.append(_normalize_lora_basename(name))
    return pulled


def _normalize_lora_basename(raw: str) -> str:
    if not raw:
        return ""
    path_part = raw.split(":")[0].strip().replace("\\", "/")
    return Path(path_part).name


def _lora_basenames_match(a: str, b: str) -> bool:
    return _normalize_lora_basename(a).lower() == _normalize_lora_basename(b).lower()


def extract_required_loras_from_assign(payload: dict) -> list[str]:
    """LoRA names referenced by an assign_job graph (canonical names from API)."""
    seen: set[str] = set()
    out: list[str] = []
    graph = payload.get("graph") or {}
    lora_loaders = ("LoraLoader", "LoraLoaderModelOnly", "Power Lora Loader (rgthree)")
    for node in graph.values():
        if not isinstance(node, dict):
            continue
        ct = node.get("class_type", "")
        inputs = node.get("inputs") or {}
        if not isinstance(inputs, dict):
            continue
        if ct not in lora_loaders:
            continue
        names: list[str] = []
        for key in ("lora_name", "lora"):
            val = inputs.get(key)
            if isinstance(val, str) and val.strip():
                names.append(val.strip())
        if ct == "Power Lora Loader (rgthree)":
            for slot_key, slot_val in inputs.items():
                if not isinstance(slot_key, str) or not slot_key.startswith("lora_"):
                    continue
                if not isinstance(slot_val, dict):
                    continue
                slot_lora = slot_val.get("lora")
                if isinstance(slot_lora, str) and slot_lora.strip():
                    names.append(slot_lora.strip())
        for name in names:
            key = _normalize_lora_basename(name).lower()
            if key and key not in seen:
                seen.add(key)
                out.append(name)
    return out


def worker_has_lora(state: dict, lora_name: str) -> bool:
    if not lora_name:
        return True
    target = _normalize_lora_basename(lora_name).lower()
    if not target:
        return True
    for k in state.get("known_loras") or []:
        if _lora_basenames_match(k, target):
            return True
    for k in (state.get("models") or {}).get("loras") or []:
        if _lora_basenames_match(k, target):
            return True
    mode = (os.environ.get("LOBO_MODE") or os.environ.get("MODE") or "").strip().lower()
    if mode == "wan-native" or os.environ.get("LOBO_EXECUTOR", "").strip().lower() == "native":
        for d in _native_wan_lora_dirs():
            if (d / target).is_file() or (d / lora_name).is_file():
                return True
            for f in d.glob("*.safetensors"):
                if _lora_basenames_match(f.name, target):
                    return True
    return False


async def _sync_lora_inventory(state: dict, args) -> None:
    models = await agent.get_available_models(args.comfyui_http)
    state["models"] = models
    known = state.setdefault("known_loras", [])
    for name in models.get("loras") or []:
        base = _normalize_lora_basename(name)
        if base and not any(_lora_basenames_match(k, base) for k in known):
            known.append(base)


def resolve_claim_ready_capabilities(
    state: dict,
    capabilities: list[str] | tuple[str, ...],
    *,
    hostname: str | None = None,
) -> list[str]:
    """Capabilities this worker should poll — only those with required models on disk."""
    return [
        cap for cap in capabilities
        if worker_can_poll_capability(state, cap, hostname=hostname)
    ]


def build_check_in_payload(args: argparse.Namespace, agent_state: dict[str, Any]) -> dict[str, Any]:
    gpu_info = agent.get_gpu_info()
    models = agent_state.get("models") or {}
    caps = agent_state.get("forge_queue_capabilities") or []
    claim_ready = resolve_claim_ready_capabilities(agent_state, caps, hostname=args.hostname)
    payload = {
        "node_uuid": args.node_uuid,
        "hostname": args.hostname,
        "gpu_name": gpu_info.get("gpu_name", ""),
        "vram_total": gpu_info.get("vram_total", 0),
        "vram_free": agent.get_vram_free(),
        "disk_free_mb": agent.get_disk_free_mb(),
        "models": _models_for_check_in(models),
        "known_loras": agent_state.get("known_loras", []),
        "busy": agent.agent_fleet_busy(agent_state),
        "current_job_uuid": agent_state.get("current_job"),
        "comfy_ok": agent_state.get("comfy_ok", True),
        "fleet_mode": agent.resolve_fleet_mode(args),
        "provision_mode": agent.resolve_fleet_mode(args),
        "forge_queue_capabilities": list(caps),
        "claim_ready_capabilities": claim_ready,
        "capabilities": {
            "wd14": agent.WD14_AVAILABLE,
            "joycaption": agent.JOYCAPTION_AVAILABLE,
        },
    }
    gen_mode = (os.environ.get("LOBO_GEN_QUEUE") or "").strip().lower()
    if gen_mode in ("sqs", "eventforge") or caps or os.environ.get("EVENT_FORGE_URL"):
        payload.update(probe_event_forge_access(list(caps)))
    return payload


def _env_flag(name: str, *, default: bool = True) -> bool:
    raw = (os.environ.get(name) or "").strip().lower()
    if not raw:
        return default
    return raw not in ("0", "false", "no", "off")


def _ltx23_enabled() -> bool:
    return _env_flag("LOBO_LTX23", default=False)


def _music_enabled() -> bool:
    return _env_flag("LOBO_MUSIC", default=True)


def _model_assets(state: dict) -> list[str]:
    models = state.get("models") or {}
    assets: list[str] = []
    for key in ("unets", "checkpoints", "loras", "clips", "text_encoders", "vae"):
        for name in models.get(key) or []:
            if isinstance(name, str) and name.strip():
                assets.append(name.strip())
    return assets


def _models_for_check_in(models: dict) -> dict:
    """Omit LTX weights from check-in when LOBO_LTX23=0 so claim gate skips ltx23 jobs."""
    if _ltx23_enabled() or not models:
        return models
    filtered: dict[str, Any] = {}
    for key, names in models.items():
        if not isinstance(names, list):
            filtered[key] = names
            continue
        filtered[key] = [
            n for n in names
            if isinstance(n, str) and n.strip() and not _looks_like_ltx_asset(n)
        ]
    return filtered


def _hostname_is_image_only(hostname: str | None) -> bool:
    hn = (hostname or "").lower()
    return "-image-" in hn and "-all-" not in hn


def _hostname_is_wan_or_video_only(hostname: str | None) -> bool:
    hn = (hostname or "").lower()
    if "-all-" in hn:
        return False
    return "-wan-" in hn or "-video-" in hn


def _has_flux_klein(assets: list[str]) -> bool:
    for m in assets:
        ml = m.lower()
        if "klein" in ml or "flux2" in ml or "flux-2" in ml:
            return True
    return False


def _has_flux2_text_encoder(assets: list[str]) -> bool:
    for m in assets:
        if "qwen" in m.lower():
            return True
    return False


def _has_zimage_text_encoder(assets: list[str]) -> bool:
    for m in assets:
        if "zimage" in m.lower():
            return True
    return False


def _has_lens_text_encoder(assets: list[str]) -> bool:
    for m in assets:
        if "gpt_oss" in m.lower() or "gpt-oss" in m.lower():
            return True
    return False


def _wan_noise_pair_present(names: list[str], *, kind: str) -> bool:
    """True only when both high-noise and low-noise UNETs for kind (i2v/t2v) are listed.

    Comfy Wan 2.2 MoE workflows require both stages; claiming on high-only causes
    prompt validation 400 (low_noise unet_name not in list).
    """
    matched: list[str] = []
    for m in names:
        if not isinstance(m, str):
            continue
        ml = m.lower()
        if kind == "i2v":
            if "wan" in ml and "i2v" in ml:
                matched.append(ml)
        elif ("wan" in ml and "t2v" in ml) or "t2v_low_noise" in ml or "wan2.2_t2v" in ml:
            matched.append(ml)
    if not matched:
        return False
    has_high = any("high_noise" in u or "high-noise" in u for u in matched)
    has_low = any("low_noise" in u or "low-noise" in u for u in matched)
    return has_high and has_low


def _has_wan_i2v(models: dict) -> bool:
    return _wan_noise_pair_present(list(models.get("unets") or []), kind="i2v")


def _has_wan_t2v(models: dict) -> bool:
    return _wan_noise_pair_present(list(models.get("unets") or []), kind="t2v")


def _has_ace_step(assets: list[str]) -> bool:
    for m in assets:
        ml = m.lower()
        if "ace_step" in ml or "ace-step" in ml:
            return True
    return False


def _looks_like_ltx_asset(name: str) -> bool:
    m = name or ""
    ml = m.lower()
    if "taeltx" in ml:
        return False
    if "ltx-2.3" in ml or "ltx-2" in ml or "ltx23" in ml or "ltx2" in ml:
        return True
    if "ltx" in ml and "gemma" in ml:
        return True
    return "gemma_3_12b" in ml


def worker_can_run_model(
    state: dict,
    model: str | None,
    *,
    hostname: str | None = None,
    capability: str | None = None,
) -> bool:
    """Mirror GpuNodeCompatibility.NodeCanHandleModelKey — gate before claiming SQS jobs."""
    if not (model or "").strip():
        return True

    models = state.get("models") or {}
    assets = _model_assets(state)
    lower = model.strip().lower()
    hn = hostname or ""
    image_only = _hostname_is_image_only(hn)
    video_only = _hostname_is_wan_or_video_only(hn)

    # Native Wan/LTX use disk layout, not Comfy inventory — do not require assets first.
    if lower in ("wan2", "wan2flf") and _is_native_wan_box(hostname):
        return (not image_only) and _native_wan_layout_ready()
    if (
        lower.startswith("ltx")
        and lower not in ("music", "ace-step")
        and _is_native_ltx_box(hostname)
    ):
        return (not image_only) and _native_ltx_layout_ready()

    if not assets:
        return False

    if lower == "wan2t2v":
        if image_only:
            return False
        return _has_wan_t2v(models)

    if lower in ("wan2", "wan2flf"):
        if image_only:
            return False
        return _has_wan_i2v(models)

    if lower.startswith("ltx") and lower not in ("music", "ace-step"):
        if image_only:
            return False
        if not _ltx23_enabled():
            return False
        return any(_looks_like_ltx_asset(m) for m in assets)

    if lower in ("music", "ace-step"):
        if image_only:
            return False
        if "-video-" in hn.lower() or "-all-" in hn.lower():
            return _has_ace_step(assets)
        return _has_ace_step(assets)

    if video_only and (
        lower.startswith("flux") or lower in ("storyboard", "zimage", "chroma", "lens")
    ):
        return False

    if lower.startswith("flux") or lower == "storyboard":
        if lower in ("flux2klein", "flux2klein-edit", "flux2klein-dual"):
            return _has_flux_klein(assets) and _has_flux2_text_encoder(assets)
        return _has_flux_klein(assets) or any(
            "flux" in m.lower() or "klein" in m.lower() for m in assets
        )

    if lower == "chroma":
        return any("chroma" in m.lower() for m in assets)

    if lower == "zimage":
        return _has_zimage_text_encoder(assets) or any(
            tok in m.lower() for m in assets for tok in ("zimage", "z_image", "z-image")
        )

    if lower == "lens":
        return _has_lens_text_encoder(assets) or any("lens" in m.lower() for m in assets)

    if lower in ("joycaption", "joy-caption"):
        caps = state.get("capabilities") or {}
        if caps.get("joycaption"):
            return True
        return any("joycaption" in m.lower() or "joy_caption" in m.lower() for m in assets)

    if capability and capability not in ("", lower):
        expected = {
            "flux-klein": lower.startswith("flux") or lower == "storyboard",
            "flux-klein-dual": lower == "flux2klein-dual",
            "flux-klein-edit": lower in ("flux2klein-edit", "flux2klein-dual"),
            "zimage": lower in ("zimage", "lens"),
            "chroma": lower == "chroma",
            "wan": lower.startswith("wan"),
            "ltx": lower.startswith("ltx") or lower in ("music", "ace-step"),
            "dolphin": lower == "dolphin",
        }.get(capability)
        if expected is False:
            return False

    return model in assets or any(model.lower() in m.lower() for m in assets)



# Representative model keys per forge-queue capability — used to skip SQS queues this box cannot run.
_CAPABILITY_PROBE_MODEL: dict[str, str] = {
    "flux-klein": "flux2klein",
    "flux-klein-edit": "flux2klein-edit",
    "flux-klein-dual": "flux2klein-dual",
    "zimage": "zimage",
    "chroma": "chroma",
    "wan": "wan2",
    "ltx": "ltx23",
    "dolphin": "dolphin",
}


def worker_can_poll_capability(
    state: dict,
    capability: str,
    *,
    hostname: str | None = None,
) -> bool:
    """True if this worker should call ReceiveMessage on fq-{capability}-* queues."""
    cap = (capability or "").strip().lower()
    if not cap:
        return False
    if cap == "wan":
        if _is_native_wan_box(hostname):
            return _native_wan_layout_ready()
        if not _native_wan_layout_ready():
            return False
    if cap == "ltx":
        if _is_native_ltx_box(hostname):
            return _native_ltx_layout_ready()
        if _music_enabled() and not _ltx23_enabled():
            return worker_can_run_model(state, "music", hostname=hostname, capability=cap)
    probe = _CAPABILITY_PROBE_MODEL.get(cap)
    if not probe:
        return True
    return worker_can_run_model(state, probe, hostname=hostname, capability=cap)

def worker_can_run_assign(
    state: dict,
    assign: dict,
    *,
    hostname: str | None = None,
    capability: str | None = None,
) -> bool:
    model = (assign.get("model") or "").strip()
    if assign.get("caption") or model == "joycaption":
        return worker_can_run_model(state, "joycaption", hostname=hostname, capability=capability)
    return worker_can_run_model(state, model, hostname=hostname, capability=capability)
