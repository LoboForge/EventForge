"""Fresh Vast box bootstrap — Python path for all new provisions."""

from __future__ import annotations

import asyncio
import logging
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from ..bootstrap import install_agent_deps, refresh_from_server
from ..paths import find_comfy_dir
from .comfy_token import detect_comfyui_token
from .disk import apply_effective_mode, disk_preflight
from .gpu_compat import derive_agent_hostname, get_gpu_info, run_gpu_preflight
from .hf_cli import ensure_hf_cli
from .stack import ensure_comfy_stack_ready
from .status import post_status

log = logging.getLogger("worker.provision.bootstrap")

AGENT_DIR = Path("/workspace")
AGENT_LOG = Path("/workspace/loboforge-agent.log")
PROVISION_LOG = Path("/workspace/model-provision.log")
TMUX_SESSION = "loboforge-agent"
PROVISION_SESSION = "loboforge-provision"
DEFAULT_PY = "/venv/main/bin/python3"


def resolve_hf_token(explicit: str = "") -> str:
    for cand in (explicit, os.environ.get("HF_TOKEN"), os.environ.get("HUGGINGFACE_HUB_TOKEN")):
        if cand and str(cand).strip():
            return str(cand).strip()
    return ""


@dataclass
class BootstrapArgs:
    secret: str
    server: str = "wss://www.loboforge.com"
    base_url: str = "https://www.loboforge.com"
    provision_mode: str = "all"
    instance_id: str = ""
    label: str = ""
    hostname: str = ""
    node_uuid: str = ""
    hf_token: str = ""
    agent_dir: str = "/workspace"
    comfyui_http: str = "http://127.0.0.1:18188"
    comfyui_ws: str = "ws://127.0.0.1:18188"
    comfyui_dir: str | None = None
    comfyui_token: str = ""


def resolve_python() -> str:
    for cand in (DEFAULT_PY, shutil.which("python3"), sys.executable):
        if cand and Path(cand).is_file():
            return cand
    return sys.executable


def ensure_supervisord() -> None:
    if subprocess.run(["pgrep", "-x", "supervisord"], capture_output=True).returncode == 0:
        log.info("supervisord already running")
        return
    log.info("Starting supervisord")
    sock = Path("/var/run/supervisor.sock")
    sock.unlink(missing_ok=True)
    superd = shutil.which("supervisord") or "/usr/local/bin/supervisord"
    conf = Path("/etc/supervisor/supervisord.conf")
    if Path(superd).is_file() and conf.is_file():
        subprocess.Popen([superd, "-c", str(conf)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        import time

        time.sleep(5)


def export_hf_token(token: str) -> None:
    if not token:
        log.warning("No HF_TOKEN — gated model downloads may fail")
        return
    os.environ["HF_TOKEN"] = token
    os.environ["HUGGINGFACE_HUB_TOKEN"] = token
    ensure_hf_cli()
    subprocess.run(
        ["hf", "auth", "login", "--token", token, "--add-to-git-credential"],
        capture_output=True,
        check=False,
    )


def write_worker_env(args: BootstrapArgs) -> None:
    """Persist mode + fleet toggles for agent tmux restarts."""
    token, token_src = detect_comfyui_token()
    if token:
        args.comfyui_token = token
        os.environ["LOBO_COMFYUI_TOKEN"] = token
        log.info("ComfyUI token from %s", token_src)

    env_path = Path(args.agent_dir) / ".loboforge-env"
    mode_key = (args.provision_mode or "all").strip().lower()
    ltx_default = "1" if mode_key in ("all", "both") else "0"
    exports = {
        "LOBO_MODE": args.provision_mode,
        "MODE": args.provision_mode,
        "LOBO_WAN": os.environ.get("LOBO_WAN", "1"),
        "LOBO_LTX23": os.environ.get("LOBO_LTX23", ltx_default),
        "HF_TOKEN": args.hf_token or os.environ.get("HF_TOKEN", ""),
        "LOBO_COMFYUI_TOKEN": args.comfyui_token or "",
    }
    from ..capabilities import forge_queue_capabilities_for_mode

    fq_caps = ",".join(forge_queue_capabilities_for_mode(args.provision_mode))
    if fq_caps:
        exports["FORGE_QUEUE_CAPABILITY"] = fq_caps
    exports.setdefault("LOBO_BASE_URL", args.base_url.rstrip("/"))
    exports.setdefault("LOBO_GEN_QUEUE", os.environ.get("LOBO_GEN_QUEUE", "sqs"))
    for key in ("LOBO_GEN_QUEUE", "FORGE_QUEUE_REGION", "FORGE_QUEUE_BUCKET", "FORGE_QUEUE_PREFIX", "LOBO_BASE_URL"):
        if os.environ.get(key) is not None:
            exports[key] = os.environ[key]
    if os.environ.get("LOBO_MUSIC") is not None:
        exports["LOBO_MUSIC"] = os.environ["LOBO_MUSIC"]
    ak = os.environ.get("AWS_ACCESS_KEY_ID") or os.environ.get("FORGE_QUEUE_ACCESS_KEY") or ""
    sk = os.environ.get("AWS_SECRET_ACCESS_KEY") or os.environ.get("FORGE_QUEUE_SECRET_KEY") or ""
    if ak:
        exports["AWS_ACCESS_KEY_ID"] = ak
        exports["FORGE_QUEUE_ACCESS_KEY"] = ak
    if sk:
        exports["AWS_SECRET_ACCESS_KEY"] = sk
        exports["FORGE_QUEUE_SECRET_KEY"] = sk
    lines = [f'export {k}="{v}"' for k, v in exports.items() if v is not None]
    try:
        env_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    except OSError as ex:
        log.warning("Could not write %s: %s", env_path, ex)


def ensure_tmux() -> None:
    if shutil.which("tmux"):
        return
    subprocess.run(["apt-get", "update", "-qq"], check=False, capture_output=True)
    subprocess.run(["apt-get", "install", "-y", "-qq", "tmux"], check=False, capture_output=True)


def _aws_creds_present() -> bool:
    return bool(
        (os.environ.get("AWS_ACCESS_KEY_ID") and os.environ.get("AWS_SECRET_ACCESS_KEY"))
        or (os.environ.get("FORGE_QUEUE_ACCESS_KEY") and os.environ.get("FORGE_QUEUE_SECRET_KEY"))
    )


def _install_forge_queue_sdk(py: str) -> None:
    sdk_dir = Path(os.environ.get("FORGE_QUEUE_SDK_DIR", "/workspace/forge-queue/sdk"))
    if not sdk_dir.joinpath("pyproject.toml").is_file():
        bases: list[str] = []
        for key in ("EVENT_FORGE_URL", "LOBO_BASE_URL"):
            raw = (os.environ.get(key) or "").strip().rstrip("/")
            if raw and raw not in bases:
                bases.append(raw)
        if not bases:
            bases = ["https://eventforge.loboforge.com", "https://www.loboforge.com"]
        tar_path = Path("/tmp/forge-queue-sdk.tar.gz")
        for base in bases:
            subprocess.run(
                [
                    "curl", "-fsSL", "-A", "LoboForge-Worker/1.1",
                    f"{base}/agent/forge-queue-sdk.tar.gz",
                    "-o", str(tar_path),
                ],
                check=False,
                capture_output=True,
                timeout=120,
            )
            if tar_path.is_file() and tar_path.stat().st_size > 100:
                subprocess.run(["tar", "-xzf", str(tar_path), "-C", "/workspace"], check=False)
                tar_path.unlink(missing_ok=True)
                break
            tar_path.unlink(missing_ok=True)
    if sdk_dir.joinpath("pyproject.toml").is_file():
        subprocess.run([py, "-m", "pip", "install", "-q", "-U", "-e", str(sdk_dir)], check=False)


def launch_agent_tmux(args: BootstrapArgs, *, hostname: str) -> dict:
    ensure_tmux()
    py = resolve_python()
    agent_dir = Path(args.agent_dir).resolve()
    gen_queue = (os.environ.get("LOBO_GEN_QUEUE") or "sqs").strip().lower()
    use_sqs = gen_queue == "sqs"
    use_ef = gen_queue == "eventforge"
    if use_ef:
        agent_script = agent_dir / "loboforge_agent_eventforge.py"
    else:
        agent_script = agent_dir / ("loboforge_agent_sqs.py" if use_sqs else "loboforge_agent.py")
    if not agent_script.is_file():
        return {"ok": False, "error": f"Missing {agent_script}"}

    subprocess.run(["tmux", "kill-session", "-t", TMUX_SESSION], capture_output=True, check=False)

    if use_ef:
        _install_forge_queue_sdk(py)
        native_mode = (args.provision_mode or "").strip().lower() in ("wan-native", "ltx-native")
        skip_comfy = native_mode or os.environ.get("LOBO_SKIP_COMFY", "").strip() in ("1", "true", "yes")
        cmd_parts = [
            py,
            str(agent_script),
            "--secret",
            args.secret,
            "--hostname",
            hostname,
        ]
        if not skip_comfy:
            cmd_parts.extend(
                [
                    "--comfyui-http",
                    args.comfyui_http,
                    "--comfyui-ws",
                    args.comfyui_ws,
                ]
            )
    elif use_sqs:
        if not _aws_creds_present():
            return {"ok": False, "error": "AWS IAM creds missing (ForgeQueueWorker policy)"}
        _install_forge_queue_sdk(py)
        cmd_parts = [
            py,
            str(agent_script),
            "--secret",
            args.secret,
            "--hostname",
            hostname,
            "--comfyui-http",
            args.comfyui_http,
            "--comfyui-ws",
            args.comfyui_ws,
        ]
    else:
        cmd_parts = [
            py,
            "-m",
            "loboforge_worker",
            "run",
            "--server",
            args.server,
            "--secret",
            args.secret,
            "--hostname",
            hostname,
            "--comfyui-http",
            args.comfyui_http,
            "--comfyui-ws",
            args.comfyui_ws,
        ]
        if args.hf_token:
            cmd_parts.extend(["--hf-token", args.hf_token])
        if args.comfyui_token:
            cmd_parts.extend(["--comfyui-token", args.comfyui_token])

    agent_cmd = " ".join(_shell_quote(p) for p in cmd_parts)
    env_file = Path(args.agent_dir) / ".loboforge-env"
    source_env = f'[[ -f "{env_file}" ]] && . "{env_file}"; '
    # EventForge owns the worker transport and serves the authoritative agent
    # scripts. Fetching only from args.base_url (the LoboForge hub) silently
    # replaced freshly provisioned workers with its older agent copies. That
    # removed claim-ready reconciliation, so completed Wan boxes stayed at
    # claim_ready=none until a human copied scripts and restarted the agent.
    # Use an atomic temp file so a failed/partial fetch cannot truncate a
    # previously working script; retain the hub only as an availability fallback.
    eventforge_url = (os.environ.get("EVENT_FORGE_URL") or "https://eventforge.loboforge.com").rstrip("/")
    refresh_bases = list(dict.fromkeys((eventforge_url, args.base_url.rstrip("/"))))
    quoted_bases = " ".join(_shell_quote(base) for base in refresh_bases if base)
    refresh_agent = ""
    for filename in (
        "loboforge_agent.py",
        "loboforge_agent_sqs.py",
        "loboforge_agent_eventforge.py",
        "loboforge_agent_common.py",
    ):
        destination = agent_dir / filename
        temp = agent_dir / f".{filename}.download"
        refresh_agent += (
            f'for _lf_base in {quoted_bases}; do '
            f'if curl -fsSL -A "LoboForge-Worker/1.1" "$_lf_base/agent/{filename}" '
            f'-o "{temp}" 2>/dev/null; then '
            f'mv -f "{temp}" "{destination}"; break; fi; done; '
            f'rm -f "{temp}"; '
        )
    queue_exports = ""
    if use_sqs or use_ef:
        from ..capabilities import forge_queue_capabilities_for_mode

        fq_cap = os.environ.get("FORGE_QUEUE_CAPABILITY") or ",".join(
            forge_queue_capabilities_for_mode(args.provision_mode)
        )
        queue_exports = (
            f'export LOBO_GEN_QUEUE="{gen_queue}" '
            f'FORGE_QUEUE_CAPABILITY="{fq_cap}" '
            f'LOBO_BASE_URL="{args.base_url.rstrip("/")}"; '
        )
        if use_ef:
            queue_exports += (
                f'export EVENT_FORGE_URL="{os.environ.get("EVENT_FORGE_URL", "")}" '
                f'EVENT_FORGE_WORKER_KEY="{os.environ.get("EVENT_FORGE_WORKER_KEY", "")}"; '
            )
        else:
            queue_exports += (
                f'FORGE_QUEUE_REGION="{os.environ.get("FORGE_QUEUE_REGION", "us-east-2")}" '
                f'FORGE_QUEUE_BUCKET="{os.environ.get("FORGE_QUEUE_BUCKET", "")}" '
                f'FORGE_QUEUE_PREFIX="{os.environ.get("FORGE_QUEUE_PREFIX", "fq")}"; '
            )
            if os.environ.get("AWS_ACCESS_KEY_ID"):
                queue_exports += f'export AWS_ACCESS_KEY_ID="{os.environ["AWS_ACCESS_KEY_ID"]}"; '
            if os.environ.get("AWS_SECRET_ACCESS_KEY"):
                queue_exports += f'export AWS_SECRET_ACCESS_KEY="{os.environ["AWS_SECRET_ACCESS_KEY"]}"; '
    mode = (args.provision_mode or "").strip().lower()
    if mode == "wan-native":
        queue_exports += (
            'export LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=1 LOBO_LTX23=0 '
            'MODE=wan-native LOBO_MODE=wan-native; '
        )
    elif mode == "ltx-native":
        queue_exports += (
            'export LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=0 LOBO_LTX23=1 '
            'MODE=ltx-native LOBO_MODE=ltx-native; '
        )
    transport = "eventforge" if use_ef else ("sqs" if use_sqs else "ws")
    loop = (
        f"{source_env}{refresh_agent}"
        f'export PYTHONPATH="{agent_dir}${{PYTHONPATH:+:$PYTHONPATH}}"; '
        f"{queue_exports}"
        f'while true; do '
        f'[[ -f "{env_file}" ]] && . "{env_file}"; '
        f'echo "[$(date -Is)] starting agent ({transport} gen_queue={gen_queue})..." | tee -a "{AGENT_LOG}"; '
        f"{agent_cmd} 2>&1 | tee -a \"{AGENT_LOG}\"; "
        f'echo "[$(date -Is)] agent exited, restart in 5s" | tee -a "{AGENT_LOG}"; '
        f"sleep 5; done"
    )
    r = subprocess.run(["tmux", "new-session", "-d", "-s", TMUX_SESSION, loop], capture_output=True, text=True)
    if r.returncode != 0:
        return {"ok": False, "error": r.stderr or "tmux new-session failed"}
    return {"ok": True, "session": TMUX_SESSION, "hostname": hostname, "log": str(AGENT_LOG)}


def _shell_quote(value: str) -> str:
    if not value:
        return '""'
    if all(c.isalnum() or c in "/._-:" for c in value):
        return value
    return '"' + value.replace('"', '\\"') + '"'




def _model_provision_running() -> bool:
    if subprocess.run(["pgrep", "-f", "loboforge_worker provision"], capture_output=True).returncode == 0:
        return True
    return subprocess.run(["tmux", "has-session", "-t", PROVISION_SESSION], capture_output=True).returncode == 0


def launch_model_provision_tmux(args: BootstrapArgs, *, hostname: str) -> dict:
    """Download model manifests in a dedicated tmux session.

    The SQS agent (loboforge_agent_sqs.py) does not start WorkerRuntime, so bootstrap
    must kick off downloads explicitly — same early-pool-join pattern as provision_gpu.sh.
    """
    from .runner import PROVISION_DONE
    from .validate import is_provision_complete
    from ..paths import find_models_root

    mode = args.provision_mode or "all"
    base = args.base_url or args.server.replace("wss://", "https://").replace("ws://", "http://")
    root = find_models_root(args)
    if is_provision_complete(root, mode, base, args.secret):
        log.info("Model provision already complete (%s)", PROVISION_DONE)
        return {"ok": True, "skipped": True, "reason": "done"}

    if _model_provision_running():
        log.info("Model provision already running")
        return {"ok": True, "skipped": True, "reason": "running"}

    ensure_tmux()
    py = resolve_python()
    mode = args.provision_mode or "all"
    env_file = Path(args.agent_dir) / ".loboforge-env"
    agent_dir = Path(args.agent_dir).resolve()
    done_file = str(PROVISION_DONE)

    subprocess.run(["tmux", "kill-session", "-t", PROVISION_SESSION], capture_output=True, check=False)

    prov_cmd = " ".join(
        _shell_quote(p)
        for p in (
            py,
            "-m",
            "loboforge_worker",
            "provision",
            "--secret",
            args.secret,
            "--mode",
            mode,
            "--hostname",
            hostname,
        )
    )
    check_cmd = " ".join(
        _shell_quote(p)
        for p in (
            py,
            "-m",
            "loboforge_worker",
            "provision-check",
            "--secret",
            args.secret,
            "--mode",
            mode,
            "--base-url",
            args.base_url,
        )
    )
    log_path = str(PROVISION_LOG)
    loop = (
        f'[[ -f "{env_file}" ]] && . "{env_file}"; '
        f'export PYTHONPATH="{agent_dir}${{PYTHONPATH:+:$PYTHONPATH}}"; '
        f'while ! {check_cmd}; do '
        f'echo "[$(date -Is)] starting model provision (mode={mode})..." | tee -a "{log_path}"; '
        f'{prov_cmd} 2>&1 | tee -a "{log_path}"; '
        f'echo "[$(date -Is)] provision incomplete, retry in 60s" | tee -a "{log_path}"; '
        f'sleep 60; '
        f'done; '
        f'echo "[$(date -Is)] model provision complete" | tee -a "{log_path}"'
    )
    r = subprocess.run(
        ["tmux", "new-session", "-d", "-s", PROVISION_SESSION, loop],
        capture_output=True,
        text=True,
    )
    if r.returncode != 0:
        return {"ok": False, "error": r.stderr or "tmux provision session failed"}
    log.info("Model provision tmux session %s started (mode=%s)", PROVISION_SESSION, mode)
    return {"ok": True, "session": PROVISION_SESSION, "log": str(PROVISION_LOG)}

async def bootstrap_fresh_box(args: BootstrapArgs) -> dict:
    """Comfy + agent first; model downloads in loboforge-provision tmux."""
    base = args.base_url.rstrip("/")
    mode = args.provision_mode or "all"
    args.node_uuid = args.instance_id or args.node_uuid or "unknown"

    gpu_name = get_gpu_info().get("gpu_name") or "unknown"
    hn = derive_agent_hostname(
        mode=mode,
        label=args.label or None,
        instance_id=args.instance_id or None,
    )
    args.hostname = hn

    post_status(
        "provision.start",
        "ok",
        f"mode={mode} hostname={hn} gpu={gpu_name}",
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
    )

    ensure_supervisord()
    install_agent_deps(resolve_python())
    if (os.environ.get("LOBO_GEN_QUEUE") or "sqs").strip().lower() == "sqs":
        subprocess.run(
            [resolve_python(), "-m", "pip", "install", "-q", "-U", "boto3"],
            capture_output=True,
            check=False,
        )
        _install_forge_queue_sdk(resolve_python())

    args.hf_token = resolve_hf_token(args.hf_token)
    export_hf_token(args.hf_token)

    disk = disk_preflight(mode, label=args.label or "", hostname=hn)
    if disk.get("downgraded"):
        mode = disk["mode"]
        args.provision_mode = mode
        note = disk.get("note") or ""
        apply_effective_mode(mode, note)
        log.warning("Provision mode downgraded: %s", note)

    write_worker_env(args)

    disk_status = disk.get("note") or str({k: disk[k] for k in disk if k != "note"})
    post_status(
        "disk.preflight",
        "warn" if disk.get("downgraded") else ("ok" if disk.get("ok") else "error"),
        disk_status,
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
    )
    if not disk.get("ok"):
        return {"ok": False, "error": disk.get("error"), "step": "disk.preflight", "disk": disk}

    gpu = await asyncio.to_thread(run_gpu_preflight, args, mode=mode, instance_id=args.instance_id or None)
    if not gpu.get("ok"):
        err = gpu.get("error") or "GPU preflight failed"
        post_status("gpu.preflight", "error", err, base_url=base, secret=args.secret, node_uuid=args.node_uuid)
        return {"ok": False, "error": err, "step": "gpu.preflight"}

    comfy = find_comfy_dir(args=args)
    if comfy:
        args.comfyui_dir = str(comfy)
    else:
        log.warning("ComfyUI directory not found yet — agent will self-heal via tmux")

    from loboforge_agent import ensure_comfyui_running  # type: ignore

    if not await ensure_comfyui_running(args):
        post_status(
            "comfy.start",
            "warn",
            "ComfyUI not up yet — joining pool as provisioning",
            base_url=base,
            secret=args.secret,
            node_uuid=args.node_uuid,
        )

    stack = await ensure_comfy_stack_ready(
        args,
        mode=mode,
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
    )
    if not stack.get("ok"):
        log.warning("Comfy stack preflight incomplete: %s", stack.get("steps"))

    agent_dir = Path(args.agent_dir)
    agent_dir.mkdir(parents=True, exist_ok=True)
    bundle = await asyncio.to_thread(
        refresh_from_server,
        agent_dir,
        base_url=base,
        force_worker=True,
    )
    post_status(
        "agent.fetch",
        "ok" if bundle.get("worker_ok") else "warn",
        f"updated={bundle.get('updated')}",
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
    )

    launch = launch_agent_tmux(args, hostname=hn)
    if not launch.get("ok"):
        post_status(
            "agent.launch",
            "error",
            launch.get("error") or "launch failed",
            base_url=base,
            secret=args.secret,
            node_uuid=args.node_uuid,
        )
        return {"ok": False, "error": launch.get("error"), "step": "agent.launch"}

    post_status(
        "agent.launch",
        "ok",
        f"tmux session {TMUX_SESSION} hostname={hn} python bootstrap",
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
        pct=10,
    )

    prov = launch_model_provision_tmux(args, hostname=hn)
    post_status(
        "provision.downloads",
        "ok" if prov.get("ok") else "warn",
        prov.get("error") or prov.get("reason") or f"session={prov.get('session', PROVISION_SESSION)}",
        base_url=base,
        secret=args.secret,
        node_uuid=args.node_uuid,
        pct=15,
    )
    if not prov.get("ok") and not prov.get("skipped"):
        log.warning("Background model provision failed to start: %s", prov.get("error"))

    log.info(
        "Bootstrap complete — agent=%s provision=%s (hostname=%s)",
        TMUX_SESSION,
        PROVISION_SESSION,
        hn,
    )
    return {
        "ok": True,
        "hostname": hn,
        "agent_session": TMUX_SESSION,
        "provision_session": PROVISION_SESSION if prov.get("ok") else None,
        "bundle": bundle,
        "mode": mode,
        "downgrade_note": disk.get("note"),
        "note": f"Model downloads in tmux session {PROVISION_SESSION} (tail {PROVISION_LOG})",
        "provision": prov,
    }
