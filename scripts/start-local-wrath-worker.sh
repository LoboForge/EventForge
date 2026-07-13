#!/usr/bin/env bash
# Start wrath's local GPU worker on EventForge — image + wan + music; no LTX23 video.
# LOBO_LTX23=0 hides LTX weights from check-in so the box skips ltx23 jobs (music still polls ltx queue).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SECRETS="${ROOT}/secrets.local.json"
AGENT_DIR="${ROOT}/agent"
COMFY_DIR="${COMFYUI_DIR:-/media/wrath/AI/ComfyUI}"
COMFY_HTTP="${COMFYUI_HTTP:-http://127.0.0.1:8188}"
COMFY_WS="${COMFYUI_WS:-ws://127.0.0.1:8188}"
COMFY_MODELS="${COMFYUI_MODELS:-${COMFY_DIR}/models}"
FORGE_QUEUE_SDK="${FORGE_QUEUE_SDK:-${ROOT}/../LoboForge.Studio/forge-queue/sdk}"
LOG="${LOCAL_WRATH_LOG:-/tmp/local-wrath-eventforge.log}"
TMUX_SESSION="${LOCAL_WRATH_TMUX:-local-wrath-ef}"

die() { echo "ERROR: $*" >&2; exit 1; }

[[ -f "$SECRETS" ]] || die "Missing $SECRETS (copy secrets.example.json)"
[[ -d "$AGENT_DIR" ]] || die "Missing agent dir: $AGENT_DIR"
[[ -f "$COMFY_DIR/main.py" ]] || die "ComfyUI not found at $COMFY_DIR"

read_env() {
  python3 - "$SECRETS" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
ef = data.get("EventForge") or {}
lf = data.get("LoboForge") or {}
print(ef.get("PublicUrl", "https://eventforge.loboforge.com"))
print(ef.get("WorkerKey", ""))
print(lf.get("BaseUrl", "https://www.loboforge.com"))
print(lf.get("WorkersSecret", ""))
PY
}

mapfile -t _cfg < <(read_env)
EVENT_FORGE_URL="${_cfg[0]%/}"
EVENT_FORGE_WORKER_KEY="${_cfg[1]}"
LOBO_BASE_URL="${_cfg[2]%/}"
LOBO_SECRET="${_cfg[3]}"
[[ -n "$EVENT_FORGE_WORKER_KEY" ]] || die "EventForge.WorkerKey missing in secrets.local.json"
[[ -n "$LOBO_SECRET" ]] || die "LoboForge.WorkersSecret missing in secrets.local.json"

HOSTNAME="${LOCAL_WRATH_HOSTNAME:-local-wrath}"
PY="${COMFYUI_PYTHON:-${COMFY_DIR}/venv/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

# Full Comfy caps except LTX23 video. Music (ACE-Step) still polls the ltx queue when LOBO_LTX23=0.
export LOBO_GEN_QUEUE=eventforge
export LOBO_LTX23=0
export LOBO_MUSIC=1
export LOBO_WAN=1
export LOBO_MODE="${LOBO_MODE:-all}"
export FORGE_QUEUE_CAPABILITY="${FORGE_QUEUE_CAPABILITY:-flux-klein,flux-klein-edit,zimage,chroma,ltx}"
export EVENT_FORGE_URL EVENT_FORGE_WORKER_KEY LOBO_BASE_URL LOBO_SECRET
export MODELS="${COMFY_MODELS}"
export PYTHONPATH="${AGENT_DIR}${PYTHONPATH:+:$PYTHONPATH}"

# EventForge agent imports loboforge_agent_sqs (LoRA prefetch helpers) which needs forge-queue SDK.
if ! "$PY" -c "import forge_queue" >/dev/null 2>&1; then
  [[ -f "${FORGE_QUEUE_SDK}/pyproject.toml" ]] || die "forge-queue SDK missing at ${FORGE_QUEUE_SDK}"
  echo "Installing forge-queue SDK into ${PY}..."
  "$PY" -m pip install -q -e "${FORGE_QUEUE_SDK}"
fi

NODE_UUID_FILE="${HOME}/.loboforge_node_uuid"
NODE_UUID="$(cat "$NODE_UUID_FILE" 2>/dev/null || true)"
[[ -n "$NODE_UUID" ]] || NODE_UUID="$(python3 -c 'import uuid; print(uuid.uuid4())')"
printf '%s\n' "$NODE_UUID" >"$NODE_UUID_FILE"

# Optional WD14 tagger (agent still runs without it).
if [[ ! -f "${AGENT_DIR}/wd14_tagger.py" && -f "${ROOT}/../LoboForge.Studio/wd14_tagger.py" ]]; then
  cp -f "${ROOT}/../LoboForge.Studio/wd14_tagger.py" "${AGENT_DIR}/wd14_tagger.py" 2>/dev/null || true
fi

comfy_ok() {
  curl -sf --max-time 3 "${COMFY_HTTP}/system_stats" >/dev/null 2>&1
}

start_comfy() {
  if comfy_ok; then
    echo "ComfyUI already up at ${COMFY_HTTP}"
    return 0
  fi
  if tmux has-session -t comfyui 2>/dev/null; then
    echo "Waiting for ComfyUI (tmux session comfyui)..."
    for _ in $(seq 1 60); do
      comfy_ok && return 0
      sleep 2
    done
    die "ComfyUI tmux session exists but ${COMFY_HTTP} not responding"
  fi
  echo "Starting ComfyUI in tmux session 'comfyui'..."
  tmux new-session -d -s comfyui "bash -lc '
cd \"${COMFY_DIR}\"
source venv/bin/activate 2>/dev/null || true
exec python main.py --listen 127.0.0.1 --port 8188
'"
  for _ in $(seq 1 90); do
    comfy_ok && { echo "ComfyUI ready"; return 0; }
    sleep 2
  done
  die "ComfyUI failed to start on ${COMFY_HTTP}"
}

start_agent() {
  if pgrep -f '[/]loboforge_agent_eventforge.py' >/dev/null 2>&1; then
    echo "Stopping existing EventForge GPU agent..."
    pkill -f '[/]loboforge_agent_eventforge.py' 2>/dev/null || true
    sleep 2
  fi
  tmux kill-session -t "$TMUX_SESSION" 2>/dev/null || true
  : >"$LOG"
  tmux new-session -d -s "$TMUX_SESSION" "bash -lc '
export LOBO_GEN_QUEUE=\"${LOBO_GEN_QUEUE}\"
export LOBO_LTX23=\"${LOBO_LTX23}\"
export LOBO_MUSIC=\"${LOBO_MUSIC}\"
export LOBO_WAN=\"${LOBO_WAN}\"
export LOBO_MODE=\"${LOBO_MODE}\"
export FORGE_QUEUE_CAPABILITY=\"${FORGE_QUEUE_CAPABILITY}\"
export EVENT_FORGE_URL=\"${EVENT_FORGE_URL}\"
export EVENT_FORGE_WORKER_KEY=\"${EVENT_FORGE_WORKER_KEY}\"
export LOBO_BASE_URL=\"${LOBO_BASE_URL}\"
export LOBO_SECRET=\"${LOBO_SECRET}\"
export MODELS=\"${COMFY_MODELS}\"
export PYTHONPATH=\"${AGENT_DIR}\"
LOG=\"${LOG}\"
while true; do
  echo \"[\$(date -Is)] starting EventForge GPU agent caps=${FORGE_QUEUE_CAPABILITY}...\" | tee -a \"\$LOG\"
  \"${PY}\" \"${AGENT_DIR}/loboforge_agent_eventforge.py\" \
    --secret \"${LOBO_SECRET}\" \
    --node-uuid \"${NODE_UUID}\" \
    --hostname \"${HOSTNAME}\" \
    --comfyui-http \"${COMFY_HTTP}\" \
    --comfyui-ws \"${COMFY_WS}\" \
    --ef-url \"${EVENT_FORGE_URL}\" \
    2>&1 | tee -a \"\$LOG\"
  echo \"[\$(date -Is)] agent exited, restart in 5s\" | tee -a \"\$LOG\"
  sleep 5
done'"
  for _ in $(seq 1 30); do
    if grep -q 'GPU EventForge agent starting' "$LOG" 2>/dev/null; then
      tail -3 "$LOG"
      return 0
    fi
    sleep 1
  done
  die "Agent did not start — see $LOG"
}

echo "▶ EventForge local wrath worker"
echo "  EF URL:  ${EVENT_FORGE_URL}"
echo "  Host:    ${HOSTNAME} (${NODE_UUID})"
echo "  Caps:    ${FORGE_QUEUE_CAPABILITY}"
echo "  Comfy:   ${COMFY_HTTP}"

start_comfy
start_agent

if curl -sf --max-time 10 -A 'LoboForge-Worker/1.1' "${EVENT_FORGE_URL}/health" >/dev/null; then
  echo "▶ EventForge health OK"
fi

echo "▶ Worker running"
echo "  tmux attach -t ${TMUX_SESSION}   # agent logs"
echo "  tmux attach -t comfyui           # ComfyUI"
echo "  tail -f ${LOG}"
