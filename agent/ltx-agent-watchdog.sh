#!/usr/bin/env bash
# On-box LTX watchdog — cron every 5 min. Vast remote execute cannot run curl|bash,
# so the API babysitter cannot heal us; this script can.
set -euo pipefail

mkdir -p /workspace
cd /workspace
[[ -f /workspace/.loboforge-env ]] && set -a && . /workspace/.loboforge-env && set +a

BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
case "$(printf '%s' "$BASE" | tr '[:upper:]' '[:lower:]')" in
  *eventforge.loboforge.com*) BASE="https://www.loboforge.com" ;;
esac
BASE="${BASE%/}"
LF_UA="LoboForge-Worker/1.1"

# Durable hf-hub pin: the reconnect path below reinstalls the worker + native deps,
# which otherwise re-upgrade huggingface_hub to 1.x and break the transformers 4.x
# LTX runner (see provision_ltx_native.sh). Carry the persisted constraint file.
if [[ -f /workspace/pip-constraints.txt ]]; then
  export PIP_CONSTRAINT="${PIP_CONSTRAINT:-/workspace/pip-constraints.txt}"
fi

# Vast images often reboot without starting cron — heal that every run.
if command -v service >/dev/null 2>&1; then
  service cron start 2>/dev/null || true
elif command -v cron >/dev/null 2>&1; then
  pgrep -x cron >/dev/null 2>&1 || cron 2>/dev/null || true
fi
if [[ ! -x /workspace/ltx-agent-watchdog.sh ]]; then
  curl -fsSL -A "$LF_UA" "${BASE}/agent/ltx-agent-watchdog.sh" -o /workspace/ltx-agent-watchdog.sh 2>/dev/null || true
  chmod +x /workspace/ltx-agent-watchdog.sh 2>/dev/null || true
fi
(crontab -l 2>/dev/null | grep -v ltx-agent-watchdog.sh || true
 echo '*/5 * * * * bash /workspace/ltx-agent-watchdog.sh >> /workspace/ltx-watchdog.log 2>&1') | crontab - 2>/dev/null || true

if tmux has-session -t loboforge-agent 2>/dev/null; then
  exit 0
fi
PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"
LTX_ROOT="${LTX_MODEL_ROOT:-/workspace/ltx-models}"
GEMMA_DIR="${LTX_ROOT}/text_encoders/gemma-hf"

# Heal missing Gemma repo marker when HF shards are already on disk (legacy boxes).
if [[ -f "${GEMMA_DIR}/config.json" ]] && compgen -G "${GEMMA_DIR}/model"*.safetensors >/dev/null; then
  if [[ ! -s "${GEMMA_DIR}/.loboforge-gemma-repo" ]]; then
    echo "${LOBO_LTX_GEMMA_REPO:-google/gemma-3-12b-it-qat-q4_0-unquantized}" > "${GEMMA_DIR}/.loboforge-gemma-repo"
  fi
fi

if [[ ! -f "${LTX_ROOT}/checkpoints/ltx-2.3-22b-distilled-1.1.safetensors" ]] \
   && [[ ! -f "${LTX_ROOT}/checkpoints/ltx-2.3-22b-dev-fp8.safetensors" ]]; then
  echo "[$(date -Is)] watchdog: models not ready — skip agent launch" >> /workspace/ltx-watchdog.log
  exit 0
fi

echo "[$(date -Is)] watchdog: agent tmux missing — reconnecting" >> /workspace/ltx-watchdog.log

export LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=0 LOBO_LTX23=1 LOBO_MUSIC=0
export MODE=ltx-native LOBO_MODE=ltx-native
export LTX_MODEL_ROOT="$LTX_ROOT" LTX_REPO="${LTX_REPO:-/workspace/LTX-2}"
export LOBO_LABEL="${LOBO_LABEL:-loboforge-ltx}"
[[ -n "${CONTAINER_ID:-}" && -z "${LOBO_INSTANCE_ID:-}" ]] && export LOBO_INSTANCE_ID="${CONTAINER_ID}"

if [[ ! -d /workspace/loboforge_worker ]]; then
  curl -fsSL -A "LoboForge-Worker/1.1" "${BASE}/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz \
    || "$PY" -c "import urllib.request; open('/tmp/loboforge_worker.tar.gz','wb').write(urllib.request.urlopen(urllib.request.Request('${BASE}/agent/loboforge_worker.tar.gz', headers={'User-Agent':'LoboForge-Worker/1.1'}), timeout=120).read())"
  tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
  rm -f /tmp/loboforge_worker.tar.gz
fi

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
"$PY" -m loboforge_worker provision-ltx-native --connect-only \
  --secret "${LOBO_SECRET:?LOBO_SECRET missing in .loboforge-env}" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "${LOBO_INSTANCE_ID:-unknown}" \
  --label "$LOBO_LABEL" \
  2>&1 | tee -a /workspace/ltx-watchdog.log
