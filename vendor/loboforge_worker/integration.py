"""Hook loboforge_worker into the legacy loboforge_agent.py runtime."""

from __future__ import annotations

import logging
from typing import Any

from .commands import DEFAULT_REGISTRY, WorkerContext
from .protocol import command_result, worker_event
from .runtime import WorkerRuntime

log = logging.getLogger("worker.integration")

_runtime: WorkerRuntime | None = None
_registry = DEFAULT_REGISTRY


def start_worker_runtime(session: Any, args: Any, agent_state: dict) -> list:
    """Start modular worker management (provision, health, pull loop)."""
    global _runtime
    try:
        from .bootstrap import refresh_from_server, http_base

        agent_dir = getattr(args, "agent_dir", None) or "/workspace"
        base = http_base(getattr(args, "server", None))
        refresh_from_server(agent_dir, base_url=base, force_worker=True)
    except Exception as ex:
        log.warning("Worker bundle refresh on connect failed: %s", ex)

    _runtime = WorkerRuntime(session, args, agent_state)
    log.info("WorkerRuntime starting (mode=%s)", _runtime.provision.state.mode)
    return _runtime.start()




def _provision_running_elsewhere() -> bool:
    import subprocess
    if subprocess.run(["pgrep", "-f", "loboforge_worker provision"], capture_output=True).returncode == 0:
        return True
    return subprocess.run(["tmux", "has-session", "-t", "loboforge-provision"], capture_output=True).returncode == 0


def _sqs_provision_args(args: Any) -> Any:
    import os

    class ProvisionArgs:
        pass

    pa = ProvisionArgs()
    pa.secret = args.secret
    pa.hostname = getattr(args, "hostname", "") or ""
    pa.node_uuid = getattr(args, "node_uuid", "") or "unknown"
    pa.server = os.environ.get("LOBO_SERVER", "wss://www.loboforge.com")
    pa.provision_mode = (
        os.environ.get("LOBO_MODE")
        or os.environ.get("MODE")
        or "all"
    )
    pa.hf_token = os.environ.get("HF_TOKEN")
    pa.comfyui_dir = getattr(args, "comfyui_dir", None)
    pa.comfyui_http = getattr(args, "comfyui_http", "http://127.0.0.1:18188")
    pa.comfyui_ws = getattr(args, "comfyui_ws", "ws://127.0.0.1:18188")
    pa.label = os.environ.get("LOBO_LABEL", "")
    return pa


async def start_sqs_background_provision(args: Any, agent_state: dict) -> "asyncio.Task | None":
    """Start model downloads for SQS agents (no WorkerRuntime on connect)."""
    import asyncio
    import os
    from .provision.runner import ProvisionRunner
    from .provision.validate import is_provision_complete
    from .paths import find_models_root
    from .events import EventBus

    pa = _sqs_provision_args(args)
    base = pa.server.replace("wss://", "https://").replace("ws://", "http://")
    root = find_models_root(pa)
    if is_provision_complete(root, pa.provision_mode or "image", base, pa.secret):
        return None
    if _provision_running_elsewhere():
        log.info("Model provision already running (tmux or separate process)")
        return None
    runner = ProvisionRunner(pa, EventBus())
    agent_state["provisioning"] = True
    agent_state["provision_step"] = "starting"
    agent_state["provision_pct"] = 0
    runner.start_background()
    log.info("SQS agent starting background model provision (mode=%s)", pa.provision_mode)

    async def _watch() -> None:
        try:
            if runner._task:
                result = await runner._task
                agent_state["provisioning"] = False
                if isinstance(result, dict) and result.get("ok"):
                    agent_state["provision_step"] = "ready"
                    agent_state["provision_pct"] = 100
                    log.info("Background model provision complete")
                else:
                    err = result.get("error") if isinstance(result, dict) else str(result)
                    log.warning("Background model provision failed: %s", err)
        except Exception as ex:
            agent_state["provisioning"] = False
            log.warning("Background model provision error: %s", ex)

    return asyncio.create_task(_watch())

def start_background_services(args: Any) -> None:
    """Legacy entry — provision only when runtime is not used."""
    from .provision.runner import ProvisionRunner
    from .events import EventBus
    ProvisionRunner(args, EventBus()).start_background()


async def handle_command_message(msg: dict, session: Any, args: Any, agent_state: dict) -> None:
    command_id = msg.get("command_id") or msg.get("id") or ""
    command = msg.get("command") or ""
    cmd_args = msg.get("args") or {}
    if not command_id or not command:
        await session.send_json(command_result(command_id or "?", False, error="missing command_id or command"))
        return

    async def get_models():
        from loboforge_agent import get_available_models  # type: ignore
        return await get_available_models(args.comfyui_http)

    async def restart_comfy():
        from loboforge_agent import ensure_comfyui_running  # type: ignore
        return await ensure_comfyui_running(args)

    provision = _runtime.provision if _runtime else None
    if provision is None:
        from .provision.runner import ProvisionRunner
        from .events import EventBus
        provision = ProvisionRunner(args, EventBus())

    ctx = WorkerContext(
        args=args,
        agent_state=agent_state,
        session=session,
        provision=provision,
        get_models=get_models,
        restart_comfy=restart_comfy,
    )
    try:
        log.info("Command %s (%s)", command, command_id[:8])
        result = await _registry.dispatch(command, cmd_args if isinstance(cmd_args, dict) else {}, ctx)
        await session.send_json(command_result(command_id, True, result=result))
        await session.send_json(worker_event("command.completed", {"command": command, "command_id": command_id}))
    except Exception as ex:
        log.warning("Command %s failed: %s", command, ex)
        await session.send_json(command_result(command_id, False, error=str(ex)))
