#!/usr/bin/env bash
# EventForge Ollama agent restart loop (loboforge-ollama Vast boxes).
set -euo pipefail
source /workspace/.loboforge-env 2>/dev/null || true
source /workspace/worker-bootstrap-env.sh 2>/dev/null || true
PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
export LOBO_SECRET="${LOBO_SECRET:-}"
export LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
export EVENT_FORGE_URL="${EVENT_FORGE_URL:-http://eventforge.loboforge.local:8090}"
export EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:-}"
LOG=/workspace/loboforge-ollama-agent.log
while true; do
  source /workspace/.loboforge-env 2>/dev/null || true
  echo "[$(date -Is)] starting Ollama EventForge agent..." | tee -a "$LOG"
  "$PY" /workspace/loboforge_ollama_agent_eventforge.py \
    --secret "${LOBO_SECRET}" \
    2>&1 | tee -a "$LOG"
  echo "[$(date -Is)] agent exited, restart in 5s" | tee -a "$LOG"
  sleep 5
done
