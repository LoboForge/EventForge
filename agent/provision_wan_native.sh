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

"$PY" -m pip install -q -U websockets aiohttp gdown huggingface_hub safetensors 2>/dev/null || true

BASE="${LOBO_BASE_URL:-https://eventforge.loboforge.com}"
BASE="${BASE%/}"
EF_BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EF_BASE="${EF_BASE%/}"

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

for agent_url in "${EF_BASE}/agent/loboforge_agent_eventforge.py" "${BASE}/agent/loboforge_agent_eventforge.py"; do
  lf_fetch "$agent_url" /workspace/loboforge_agent_eventforge.py 2>/dev/null && break
done
lf_fetch "${BASE}/agent/loboforge_agent.py" /workspace/loboforge_agent.py 2>/dev/null || true
lf_fetch "${BASE}/agent/loboforge_agent_common.py" /workspace/loboforge_agent_common.py 2>/dev/null || true

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

if ! "$PY" -m loboforge_worker provision-wan-native --help 2>/dev/null | grep -q connect-only; then
  echo "ERROR: loboforge_worker missing provision-wan-native — deploy updated worker tarball" | tee -a /workspace/provision.log
  exit 1
fi

"$PY" -m loboforge_worker provision-wan-native --connect-only \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  2>&1 | tee -a /workspace/provision.log

nohup "$PY" -m loboforge_worker provision-wan-native --skip-agent-launch \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-wan-native}" \
  --hf-token "$HF_TOKEN" \
  >> /workspace/provision.log 2>&1 &
echo "background Wan native downloads pid=$!" | tee -a /workspace/provision.log
