#!/bin/bash
# Surgical restart of the EventForge WAN agent supervisor loop on a Vast box.
# Recreates the `loboforge-agent` tmux session with the same while-true watchdog
# that provision uses, WITHOUT re-running model provision or Comfy prep.
# Reads all worker config from /workspace/.loboforge-env (written at first provision).
set -euo pipefail

ENV_FILE=/workspace/.loboforge-env
AGENT=/workspace/loboforge_agent_eventforge.py
LOG=/workspace/loboforge-agent.log
PY=/venv/main/bin/python3
SESSION=loboforge-agent

[[ -f "$ENV_FILE" ]] || { echo "FATAL: $ENV_FILE missing"; exit 1; }
[[ -f "$AGENT" ]]    || { echo "FATAL: $AGENT missing"; exit 1; }
[[ -x "$PY" ]]       || PY="$(command -v python3)"

set -a; . "$ENV_FILE"; set +a

: "${LOBO_SECRET:?LOBO_SECRET missing from env}"
# Hostname must never hard-fail the restart: fall back to label / container id / hostname
# so a box that predates the persisted LOBO_HOSTNAME still recovers.
HN="${LOBO_HOSTNAME:-${LOBO_LABEL:-loboforge-video-${CONTAINER_ID:-$(hostname)}}}"

# Detect the running ComfyUI port (differs per box: 8188 vs 18188).
PORT="$( { grep -oE -- '--port [0-9]+' /tmp/comfyui.log 2>/dev/null || true; } | grep -oE '[0-9]+' | head -1 || true)"
if [[ -z "${PORT:-}" ]]; then
  PORT="$( { ss -ltn 2>/dev/null || true; } | grep -oE '127.0.0.1:(8188|18188)' | grep -oE '[0-9]+$' | head -1 || true)"
fi
PORT="${PORT:-8188}"
COMFY_HTTP="http://127.0.0.1:${PORT}"
COMFY_WS="ws://127.0.0.1:${PORT}/ws"

# Kill any orphaned agent process + stale session, then relaunch supervised.
pkill -f '[l]oboforge_agent_eventforge.py' 2>/dev/null || true
tmux kill-session -t "$SESSION" 2>/dev/null || true
sleep 1

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
export PATH="$(dirname "$PY"):$PATH"

tmux new-session -d -s "$SESSION" "bash -lc '
  set -a; . \"$ENV_FILE\"; set +a
  export PYTHONPATH=/workspace
  export PATH=\"$(dirname "$PY"):\$PATH\"
  while true; do
    echo \"[\$(date -Is)] starting agent (eventforge wan comfy=$COMFY_HTTP)\" | tee -a \"$LOG\";
    \"$PY\" \"$AGENT\" --secret \"\$LOBO_SECRET\" --hostname \"$HN\" --comfyui-http \"$COMFY_HTTP\" --comfyui-ws \"$COMFY_WS\" 2>&1 | tee -a \"$LOG\";
    echo \"[\$(date -Is)] agent exited, restart in 5s\" | tee -a \"$LOG\";
    sleep 5;
  done
'"

sleep 2
if tmux has-session -t "$SESSION" 2>/dev/null; then
  echo "OK: session=$SESSION hostname=$HN comfy=$COMFY_HTTP"
  tmux ls
else
  echo "FATAL: session $SESSION did not start"; exit 1
fi
