#!/usr/bin/env bash
# Onstart entry for Wan 2.2 native boxes (served at /agent/provision_wan_native.sh).
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

export LOBO_EXECUTOR="${LOBO_EXECUTOR:-native}"
export LOBO_SKIP_COMFY="${LOBO_SKIP_COMFY:-1}"
export LOBO_WAN="${LOBO_WAN:-1}"
export LOBO_LTX23="${LOBO_LTX23:-0}"
export LOBO_MUSIC="${LOBO_MUSIC:-0}"
export LOBO_UNLOAD_MODELS="${LOBO_UNLOAD_MODELS:-0}"
export MODE="${MODE:-wan-native}"
export LOBO_MODE="${LOBO_MODE:-wan-native}"
export WAN_MODEL_ROOT="${WAN_MODEL_ROOT:-/workspace/wan-models}"
export WAN_REPO="${WAN_REPO:-/workspace/Wan2.2}"

LOBO_SECRET="${LOBO_SECRET:-change-me-in-admin}"
if [[ -z "${LOBO_INSTANCE_ID:-}" ]]; then
  LOBO_INSTANCE_ID="${CONTAINER_ID:-unknown}"
fi
HF_TOKEN="${HF_TOKEN:-}"
export LOBO_SECRET LOBO_INSTANCE_ID HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

EF_BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EF_BASE="${EF_BASE%/}"
BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"
LF_UA="LoboForge-Worker/1.1"

lf_fetch() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1 && curl -fsSL -A "$LF_UA" "$url" -o "$dest" 2>/dev/null; then return 0; fi
  LOBO_FETCH_URL="$url" LOBO_FETCH_DEST="$dest" LOBO_FETCH_UA="$LF_UA" "$PY" - <<'PY'
import os, urllib.request
req = urllib.request.Request(os.environ["LOBO_FETCH_URL"], headers={"User-Agent": os.environ.get("LOBO_FETCH_UA", "LoboForge-Worker/1.1")})
with urllib.request.urlopen(req, timeout=120) as resp, open(os.environ["LOBO_FETCH_DEST"], "wb") as out:
    out.write(resp.read())
PY
}

# Shared bootstrap helpers (forge-queue install, env merge, agent fetch).
for bootstrap_url in "${EF_BASE}/agent/worker-bootstrap-env.sh" "${BASE}/agent/worker-bootstrap-env.sh"; do
  if lf_fetch "$bootstrap_url" /workspace/worker-bootstrap-env.sh; then break; fi
done
if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/workspace/worker-bootstrap-env.sh
  source /workspace/worker-bootstrap-env.sh
  type lobo_ensure_ops_ssh &>/dev/null && lobo_ensure_ops_ssh || true
  type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || true
  type lobo_fetch_agent_scripts &>/dev/null && lobo_fetch_agent_scripts /workspace || true
  if type lobo_write_persisted_env &>/dev/null; then
    lobo_write_persisted_env /workspace/.loboforge-env
  fi
fi

cuda_ok() { "$PY" -c 'import torch,sys; sys.exit(0 if (torch.cuda.is_available() and (torch.zeros(1,device="cuda")+1).item()==1.0) else 1)' >/dev/null 2>&1; }
if ! cuda_ok; then
  echo "CUDA smoke test failed — neutralizing forward-compat libcuda" | tee -a /workspace/provision.log
  mkdir -p /workspace/.cuda-compat-disabled
  for d in /usr/local/cuda/compat /usr/local/cuda-*/compat; do
    [[ -d "$d" ]] || continue
    mv "$d"/libcuda.so* "/workspace/.cuda-compat-disabled/" 2>/dev/null || true
  done
  ldconfig 2>/dev/null || true
fi

"$PY" -m pip install -q -U websockets aiohttp gdown huggingface_hub safetensors 2>/dev/null || true

rm -rf /workspace/loboforge_worker
for tarball_url in "${EF_BASE}/agent/loboforge_worker.tar.gz" "${BASE}/agent/loboforge_worker.tar.gz"; do
  if lf_fetch "$tarball_url" /tmp/loboforge_worker.tar.gz 2>/dev/null; then break; fi
done
if [[ -f /tmp/loboforge_worker.tar.gz ]]; then
  TMP_EX="$(mktemp -d)"
  tar -xzf /tmp/loboforge_worker.tar.gz -C "$TMP_EX"
  rm -rf /workspace/loboforge_worker
  if [[ -f "$TMP_EX/loboforge_worker/__init__.py" ]]; then
    mv "$TMP_EX/loboforge_worker" /workspace/
  elif [[ -f "$TMP_EX/__init__.py" ]]; then
    mv "$TMP_EX" /workspace/loboforge_worker
  fi
  rm -rf "$TMP_EX" /tmp/loboforge_worker.tar.gz
  export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
  "$PY" -c "import loboforge_worker"
fi

for agent_file in loboforge_agent_eventforge.py loboforge_agent_sqs.py loboforge_agent_common.py loboforge_agent.py; do
  fetched=""
  for agent_url in "${EF_BASE}/agent/${agent_file}" "${BASE}/agent/${agent_file}"; do
    if lf_fetch "$agent_url" "/workspace/${agent_file}" 2>/dev/null; then fetched=1; break; fi
  done
  [[ -n "$fetched" || "$agent_file" == loboforge_agent.py ]] || echo "WARN: could not fetch ${agent_file}" | tee -a /workspace/provision.log
done

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

# Mandatory LoRA sync from loboforge.com before agent claims jobs.
LORA_MODE="video"
"$PY" -m loboforge_worker sync-loras \
  --base-url "$BASE" \
  --secret "$LOBO_SECRET" \
  --mode "$LORA_MODE" \
  2>&1 | tee -a /workspace/lora-sync.log || \
  echo "WARN: initial sync-loras failed — agent will retry via ef_lora_sync_loop" | tee -a /workspace/provision.log

if ! "$PY" -m loboforge_worker provision-wan-native --help 2>/dev/null | grep -q connect-only; then
  echo "ERROR: loboforge_worker missing provision-wan-native — deploy updated worker tarball" | tee -a /workspace/provision.log
  exit 1
fi

# Early fleet join — warn-only so background downloads always start.
if ! "$PY" -m loboforge_worker provision-wan-native --connect-only \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  2>&1 | tee -a /workspace/provision.log; then
  echo "WARN: connect-only failed — watchdog will retry agent launch" | tee -a /workspace/provision.log
fi

nohup "$PY" -m loboforge_worker provision-wan-native --skip-agent-launch \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  >> /workspace/provision.log 2>&1 &
echo "background Wan native downloads pid=$!" | tee -a /workspace/provision.log

for wd_url in "${EF_BASE}/agent/wan-agent-watchdog.sh" "${BASE}/agent/wan-agent-watchdog.sh"; do
  lf_fetch "$wd_url" /workspace/wan-agent-watchdog.sh 2>/dev/null && break
done
chmod +x /workspace/wan-agent-watchdog.sh 2>/dev/null || true
service cron start 2>/dev/null || (command -v cron >/dev/null && pgrep -x cron >/dev/null || cron) 2>/dev/null || true
(crontab -l 2>/dev/null | grep -v wan-agent-watchdog.sh || true
 echo '*/5 * * * * bash /workspace/wan-agent-watchdog.sh >> /workspace/wan-watchdog.log 2>&1') | crontab - 2>/dev/null || true
