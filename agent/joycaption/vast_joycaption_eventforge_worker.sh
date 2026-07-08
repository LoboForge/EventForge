#!/usr/bin/env bash
# Start JoyCaption EventForge worker on a Vast box (no MQTT / no coordinator).
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=vast_joycaption_health.sh
source "$DIR/vast_joycaption_health.sh"

export HF_HOME="${HF_HOME:-/workspace/joycaption/hf_cache}"
export TRANSFORMERS_CACHE="${TRANSFORMERS_CACHE:-$HF_HOME}"
export JOYCAPTION_PYTHON="${JOYCAPTION_PYTHON:-/workspace/joycaption/venv/bin/python3}"
export JOYCAPTION_SERVER_PY="${JOYCAPTION_SERVER_PY:-/workspace/joycaption/joycaption_server.py}"
export JOYCAPTION_PROMPT="${JOYCAPTION_PROMPT:-/workspace/joycaption/joycaption_prompt.json}"
export JOYCAPTION_PREPEND="${JOYCAPTION_PREPEND:-}"
export JOYCAPTION_WORKER_ID="${JOYCAPTION_WORKER_ID:-vast-$(hostname -s)}"
export EVENT_FORGE_URL="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
export EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:?set EVENT_FORGE_WORKER_KEY}"
export EVENT_FORGE_CHECK_IN_SEC="${EVENT_FORGE_CHECK_IN_SEC:-30}"
export EVENT_FORGE_CLAIM_IDLE="${EVENT_FORGE_CLAIM_IDLE:-0.1}"
export EVENT_FORGE_CHECK_IN_SEC="${EVENT_FORGE_CHECK_IN_SEC:-15}"
export JOYCAPTION_BATCH_SIZE="${JOYCAPTION_BATCH_SIZE:-40}"
export JOYCAPTION_BUFFER_DEPTH="${JOYCAPTION_BUFFER_DEPTH:-40}"
export JOYCAPTION_MAX_PIPELINE="${JOYCAPTION_MAX_PIPELINE:-120}"
export JOYCAPTION_CLAIM_LOW_WATER="${JOYCAPTION_CLAIM_LOW_WATER:-40}"
export JOYCAPTION_DOWNLOAD_CONCURRENCY="${JOYCAPTION_DOWNLOAD_CONCURRENCY:-12}"
export JOYCAPTION_UPLOAD_CONCURRENCY="${JOYCAPTION_UPLOAD_CONCURRENCY:-12}"
export JOYCAPTION_GPU_BATCH_SIZE="${JOYCAPTION_GPU_BATCH_SIZE:-6}"
export JOYCAPTION_GPU_BATCH_WAIT_SEC="${JOYCAPTION_GPU_BATCH_WAIT_SEC:-0.12}"
export JOYCAPTION_DOWNLOAD_RETRIES="${JOYCAPTION_DOWNLOAD_RETRIES:-5}"
export JOYCAPTION_DOWNLOAD_RETRY_SEC="${JOYCAPTION_DOWNLOAD_RETRY_SEC:-0.75}"
export EVENT_FORGE_CLAIM_IDLE="${EVENT_FORGE_CLAIM_IDLE:-0.5}"
WORKER_PY="${JOYCAPTION_EF_WORKER_PY:-/workspace/joycaption/joycaption_eventforge_worker.py}"
WATCHDOG="${JOYCAPTION_WATCHDOG_SCRIPT:-/workspace/joycaption/watchdog.sh}"
LOG="${JOYCAPTION_WORKER_LOG:-/workspace/joycaption/worker.log}"

"$JOYCAPTION_PYTHON" -m pip install -q aiohttp 2>/dev/null || true

if pgrep -f '[/]joycaption_eventforge_worker.py' >/dev/null 2>&1; then
  if joycaption_ef_worker_healthy "$LOG"; then
    echo "JoyCaption EventForge worker already running (healthy)"
  else
    echo "JoyCaption EventForge worker stale — restarting"
    pkill -f '[/]joycaption_eventforge_worker.py' 2>/dev/null || true
    pkill -f '[/]joycaption_server.py' 2>/dev/null || true
    sleep 2
    nohup "$JOYCAPTION_PYTHON" "$WORKER_PY" >>"$LOG" 2>&1 &
    echo "JoyCaption EventForge worker restarted (log: $LOG)"
  fi
else
  nohup "$JOYCAPTION_PYTHON" "$WORKER_PY" >>"$LOG" 2>&1 &
  echo "JoyCaption EventForge worker started (log: $LOG)"
fi

if [ "${JOYCAPTION_SKIP_WATCHDOG:-0}" != "1" ] && [ -x "$WATCHDOG" ] \
    && ! pgrep -f '[/]watchdog.sh' >/dev/null 2>&1; then
  nohup bash "$WATCHDOG" >>/workspace/joycaption/watchdog.log 2>&1 &
  echo "JoyCaption watchdog started"
fi
