#!/usr/bin/env bash
# Dedicated Wan 2.2 batch box — Comfy with models kept in VRAM (LOBO_UNLOAD_MODELS=0).
# Served at /agent/provision_wan_native.sh
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

export LOBO_EXECUTOR="${LOBO_EXECUTOR:-comfy}"
export LOBO_WAN="${LOBO_WAN:-1}"
export LOBO_LTX23="${LOBO_LTX23:-0}"
export LOBO_MUSIC="${LOBO_MUSIC:-1}"
export LOBO_UNLOAD_MODELS="${LOBO_UNLOAD_MODELS:-0}"
export LOBO_HOT_MODEL="${LOBO_HOT_MODEL:-wan}"
export MODE="${MODE:-wan-native}"
export LOBO_MODE="${LOBO_MODE:-wan-native}"
export LOBO_LABEL="${LOBO_LABEL:-loboforge-wan-native}"

LOBO_SECRET="${LOBO_SECRET:-change-me-in-admin}"
if [[ -z "${LOBO_INSTANCE_ID:-}" ]]; then
  LOBO_INSTANCE_ID="${CONTAINER_ID:-unknown}"
fi
HF_TOKEN="${HF_TOKEN:-}"
export LOBO_SECRET LOBO_INSTANCE_ID HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

WAN_HI="${WAN_MODEL_MARKER:-/workspace/ComfyUI/models/diffusion_models/Wan2.2/smoothMixWan22I2VT2V_i2vHigh.safetensors}"
loboforge_wan_ready() {
  [[ -f "$WAN_HI" ]] \
    && [[ -f /workspace/.loboforge-provision-done ]]
}

"$PY" -m pip install -q -U websockets aiohttp gdown huggingface_hub 2>/dev/null || true

BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"

LF_UA="LoboForge-Worker/1.1"
lf_fetch() {
  local url="$1" dest="$2"
  if command -v curl >/dev/null 2>&1 && curl -fsSL -A "$LF_UA" "$url" -o "$dest" 2>/dev/null; then return 0; fi
  LOBO_FETCH_URL="$url" LOBO_FETCH_DEST="$dest" LOBO_FETCH_UA="$LF_UA" "$PY" - <<'PY'
import os, urllib.request
req = urllib.request.Request(
    os.environ["LOBO_FETCH_URL"],
    headers={"User-Agent": os.environ.get("LOBO_FETCH_UA", "LoboForge-Worker/1.1")},
)
with urllib.request.urlopen(req, timeout=120) as resp, open(os.environ["LOBO_FETCH_DEST"], "wb") as out:
    out.write(resp.read())
PY
}

# Reboot fast-path — models on disk, reconnect agent only.
if loboforge_wan_ready; then
  echo "fast-path: Wan batch box ready — connect-only" | tee -a /workspace/provision.log
  if [[ ! -d /workspace/loboforge_worker ]]; then
    lf_fetch "${BASE}/agent/loboforge_worker.tar.gz" /tmp/loboforge_worker.tar.gz
    tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
    rm -f /tmp/loboforge_worker.tar.gz
  fi
  lf_fetch "${BASE}/agent/loboforge_agent.py" /workspace/loboforge_agent.py
  export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
  "$PY" -m loboforge_worker provision-wan-native --connect-only \
    --secret "$LOBO_SECRET" \
    --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
    --base-url "$BASE" \
    --instance-id "$LOBO_INSTANCE_ID" \
    --label "$LOBO_LABEL" \
    --hf-token "$HF_TOKEN" \
    2>&1 | tee -a /workspace/provision.log
  exit 0
fi

rm -rf /workspace/loboforge_worker
lf_fetch "${BASE}/agent/loboforge_worker.tar.gz" /tmp/loboforge_worker.tar.gz
tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
rm -f /tmp/loboforge_worker.tar.gz
lf_fetch "${BASE}/agent/loboforge_agent.py" /workspace/loboforge_agent.py
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
"$PY" -c "import loboforge_worker"

# Early fleet join before multi-hour Wan downloads.
"$PY" -m loboforge_worker provision-wan-native --connect-only \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "$LOBO_LABEL" \
  --hf-token "$HF_TOKEN" \
  2>&1 | tee -a /workspace/provision.log

if loboforge_wan_ready; then
  echo "skip background downloads — Wan provision complete" | tee -a /workspace/provision.log
else
  nohup "$PY" -m loboforge_worker provision-wan-native --skip-agent-launch \
    --secret "$LOBO_SECRET" \
    --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
    --base-url "$BASE" \
    --instance-id "$LOBO_INSTANCE_ID" \
    --label "$LOBO_LABEL" \
    --hf-token "$HF_TOKEN" \
    >> /workspace/provision.log 2>&1 &
  echo "background Wan downloads pid=$!" | tee -a /workspace/provision.log
fi

# Music jobs use the ltx capability queue; video boxes need ACE-Step on disk.
if [[ "${LOBO_MUSIC:-1}" != "0" ]]; then
  ACE_SCRIPT="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}/agent/install_ace_step.sh"
  if curl -fsSL --max-time 30 "$ACE_SCRIPT" -o /tmp/install_ace_step.sh 2>/dev/null; then
    chmod +x /tmp/install_ace_step.sh
    nohup bash /tmp/install_ace_step.sh >> /workspace/provision.log 2>&1 &
    echo "background ACE-Step download pid=$!" | tee -a /workspace/provision.log
  fi
fi
