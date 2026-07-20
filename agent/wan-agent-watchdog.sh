#!/usr/bin/env bash
# On-box Wan native watchdog — cron every 5 min. Heals missing agent tmux after provision.
set -euo pipefail

mkdir -p /workspace
cd /workspace
[[ -f /workspace/.loboforge-env ]] && set -a && . /workspace/.loboforge-env && set +a

BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"
EF_BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EF_BASE="${EF_BASE%/}"
LF_UA="LoboForge-Worker/1.1"

if command -v service >/dev/null 2>&1; then
  service cron start 2>/dev/null || true
elif command -v cron >/dev/null 2>&1; then
  pgrep -x cron >/dev/null 2>&1 || cron 2>/dev/null || true
fi

if [[ ! -x /workspace/wan-agent-watchdog.sh ]]; then
  for wd_url in "${EF_BASE}/agent/wan-agent-watchdog.sh" "${BASE}/agent/wan-agent-watchdog.sh"; do
    curl -fsSL -A "$LF_UA" "$wd_url" -o /workspace/wan-agent-watchdog.sh 2>/dev/null && break
  done
  chmod +x /workspace/wan-agent-watchdog.sh 2>/dev/null || true
fi
(crontab -l 2>/dev/null | grep -v wan-agent-watchdog.sh || true
 echo '*/5 * * * * bash /workspace/wan-agent-watchdog.sh >> /workspace/wan-watchdog.log 2>&1') | crontab - 2>/dev/null || true

WAN_ROOT="${WAN_MODEL_ROOT:-/workspace/wan-models}"
PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

# Durable hf-hub pin: the heal/reconnect path below reinstalls agent + Wan deps, which otherwise
# re-upgrade huggingface_hub to 1.x and break the native Wan runner (see provision_wan_native.sh).
if [[ -f /workspace/pip-constraints.txt ]]; then
  export PIP_CONSTRAINT="/workspace/pip-constraints.txt"
elif ! "$PY" -c 'import transformers,sys; sys.exit(0 if int(transformers.__version__.split(".")[0])>=5 else 1)' 2>/dev/null; then
  echo 'huggingface_hub>=0.34.0,<1.0' > /workspace/pip-constraints.txt
  export PIP_CONSTRAINT="/workspace/pip-constraints.txt"
fi
if ! "$PY" -c "from loboforge_worker.inference.wan.paths import i2v_ready, load_layout, wan_model_root; r=wan_model_root(); exit(0 if (load_layout(r) and i2v_ready(r)) else 1)" 2>/dev/null; then
  echo "[$(date -Is)] watchdog: native Wan models not ready (layout/i2v) — skip agent launch" >> /workspace/wan-watchdog.log
  exit 0
fi

if tmux has-session -t loboforge-agent 2>/dev/null; then
  if ! pgrep -f 'loboforge_agent_eventforge' >/dev/null 2>&1; then
    echo "[$(date -Is)] watchdog: stale loboforge-agent tmux (no agent pid) — killing session" >> /workspace/wan-watchdog.log
    tmux kill-session -t loboforge-agent 2>/dev/null || true
  else
    exit 0
  fi
fi

echo "[$(date -Is)] watchdog: agent tmux missing — reconnecting" >> /workspace/wan-watchdog.log

# Native mode MUST keep SKIP_COMFY=1 (the native bf16 Wan runner has no ComfyUI);
# SKIP_COMFY=0 here previously flipped the agent into Comfy model checks and it
# never became claim_ready=wan. Do NOT hardcode LOBO_UNLOAD_MODELS=0 either: this
# runs AFTER sourcing .loboforge-env and would clobber the expert-swap setting,
# re-introducing the both-experts-warm OOM on 80GB. Respect the persisted value
# (default 1 = expert-swap) and carry the alloc-conf fragmentation fix.
export LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=1 LOBO_LTX23=0 LOBO_MUSIC=0
export LOBO_UNLOAD_MODELS="${LOBO_UNLOAD_MODELS:-1}"
export PYTORCH_CUDA_ALLOC_CONF="${PYTORCH_CUDA_ALLOC_CONF:-expandable_segments:True}"
export MODE=wan-native LOBO_MODE=wan-native
export WAN_MODEL_ROOT="$WAN_ROOT" WAN_REPO="${WAN_REPO:-/workspace/Wan2.2}"
export LOBO_LABEL="${LOBO_LABEL:-loboforge-wan-native}"
[[ -n "${CONTAINER_ID:-}" && -z "${LOBO_INSTANCE_ID:-}" ]] && export LOBO_INSTANCE_ID="${CONTAINER_ID}"

if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/workspace/worker-bootstrap-env.sh
  source /workspace/worker-bootstrap-env.sh
  type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || true
fi

if [[ ! -d /workspace/loboforge_worker ]]; then
  for tarball_url in "${EF_BASE}/agent/loboforge_worker.tar.gz" "${BASE}/agent/loboforge_worker.tar.gz"; do
    curl -fsSL -A "$LF_UA" "$tarball_url" -o /tmp/loboforge_worker.tar.gz 2>/dev/null && break
  done
  tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
  rm -f /tmp/loboforge_worker.tar.gz
fi

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

"$PY" -m loboforge_worker sync-loras \
  --base-url "$BASE" \
  --secret "${LOBO_SECRET:?LOBO_SECRET missing in .loboforge-env}" \
  --mode video \
  2>&1 | tee -a /workspace/lora-sync.log || true

"$PY" -m loboforge_worker provision-wan-native --connect-only \
  --secret "${LOBO_SECRET:?LOBO_SECRET missing in .loboforge-env}" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "${LOBO_INSTANCE_ID:-unknown}" \
  --label "$LOBO_LABEL" \
  --hf-token "${HF_TOKEN:-}" \
  2>&1 | tee -a /workspace/wan-watchdog.log
