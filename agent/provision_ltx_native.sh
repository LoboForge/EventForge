#!/usr/bin/env bash
# Onstart entry for LTX-only native boxes (served at /agent/provision_ltx_native.sh).
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

export LOBO_EXECUTOR="${LOBO_EXECUTOR:-native}"
export LOBO_SKIP_COMFY="${LOBO_SKIP_COMFY:-1}"
export LOBO_SKIP_LORA_PULL="${LOBO_SKIP_LORA_PULL:-1}"
export LOBO_WAN="${LOBO_WAN:-0}"
export LOBO_LTX23="${LOBO_LTX23:-1}"
export LOBO_MUSIC="${LOBO_MUSIC:-0}"
export MODE="${MODE:-ltx-native}"
export LOBO_MODE="${LOBO_MODE:-ltx-native}"
export LOBO_LTX_VARIANT="${LOBO_LTX_VARIANT:-distilled}"
export LTX_MODEL_ROOT="${LTX_MODEL_ROOT:-/workspace/ltx-models}"
export LTX_REPO="${LTX_REPO:-/workspace/LTX-2}"

# HF_TOKEN from Vast extra_env / APP_SECRETS_JSON — never commit tokens.
LOBO_SECRET="${LOBO_SECRET:-change-me-in-admin}"
# Onstart may export LOBO_INSTANCE_ID="" before CONTAINER_ID exists — treat empty as unset.
if [[ -z "${LOBO_INSTANCE_ID:-}" ]]; then
  LOBO_INSTANCE_ID="${CONTAINER_ID:-unknown}"
fi
HF_TOKEN="${HF_TOKEN:-}"
export LOBO_SECRET LOBO_INSTANCE_ID HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

# CUDA forward-compat fix: vast comfy images ship /usr/local/cuda*/compat/libcuda.so
# built for a newer driver (e.g. 575). Consumer GPUs (RTX 3090/4090) do NOT support
# forward compatibility and torch fails with "Error 804: forward compatibility was
# attempted on non supported HW". Neutralize the compat libcuda so torch uses the host
# driver via minor-version compatibility. Datacenter cards (A100/H100) keep working
# because we only act when the CUDA smoke test fails.
cuda_ok() { "$PY" -c 'import torch,sys; sys.exit(0 if (torch.cuda.is_available() and (torch.zeros(1,device="cuda")+1).item()==1.0) else 1)' >/dev/null 2>&1; }
if ! cuda_ok; then
  echo "CUDA smoke test failed — neutralizing forward-compat libcuda" | tee -a /workspace/provision.log
  mkdir -p /workspace/.cuda-compat-disabled
  for d in /usr/local/cuda/compat /usr/local/cuda-*/compat; do
    [[ -d "$d" ]] || continue
    mv "$d"/libcuda.so* "/workspace/.cuda-compat-disabled/" 2>/dev/null || true
  done
  ldconfig 2>/dev/null || true
  if cuda_ok; then
    echo "CUDA OK after disabling forward-compat libcuda" | tee -a /workspace/provision.log
  else
    echo "WARN: CUDA still failing after compat fix" | tee -a /workspace/provision.log
  fi
fi

# Heartbeat before downloads — confirms onstart reached this script.
LOBO_FETCH_URL="${LOBO_BASE_URL:-https://www.loboforge.com}/api/agent/provision-status?secret=${LOBO_SECRET}&instance_id=${LOBO_INSTANCE_ID}" \
LOBO_STATUS_BODY='{"step":"provision.shell","level":"ok","detail":"provision_ltx_native.sh started","nodeUuid":"'"${LOBO_INSTANCE_ID}"'"}' \
"$PY" - <<'PY' 2>/dev/null || true
import os, urllib.request
url = os.environ.get("LOBO_FETCH_URL", "")
body = os.environ.get("LOBO_STATUS_BODY", "{}").encode()
if url:
    urllib.request.urlopen(
        urllib.request.Request(
            url,
            data=body,
            method="POST",
            headers={"Content-Type": "application/json", "User-Agent": "LoboForge-Worker/1.1"},
        ),
        timeout=15,
    )
PY

LF_UA="LoboForge-Worker/1.1"

curl_ok() {
  command -v curl >/dev/null 2>&1 && curl --version >/dev/null 2>&1
}

lf_fetch() {
  local url="$1" dest="$2"
  if curl_ok && curl -fsSL -A "$LF_UA" "$url" -o "$dest" 2>/dev/null; then return 0; fi
  if command -v wget >/dev/null 2>&1 && wget -qO "$dest" "$url" --user-agent="$LF_UA" 2>/dev/null; then return 0; fi
  LOBO_FETCH_URL="$url" LOBO_FETCH_DEST="$dest" LOBO_FETCH_UA="$LF_UA" "$PY" - <<'PY'
import os
import urllib.request
req = urllib.request.Request(
    os.environ["LOBO_FETCH_URL"],
    headers={"User-Agent": os.environ.get("LOBO_FETCH_UA", "LoboForge-Worker/1.1")},
)
with urllib.request.urlopen(req, timeout=120) as resp, open(os.environ["LOBO_FETCH_DEST"], "wb") as out:
    out.write(resp.read())
PY
}

"$PY" -m pip install -q -U websockets aiohttp gdown "huggingface_hub>=0.36.2,<1.0" 2>/dev/null || true

BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"

# Always refresh worker package — stale trees from failed onstart runs break native provision.
rm -rf /workspace/loboforge_worker
lf_fetch "${BASE}/agent/loboforge_worker.tar.gz" /tmp/loboforge_worker.tar.gz
EXTRACT="${EXTRACT_LOBOFORGE_WORKER:-}"
if [[ -z "$EXTRACT" ]]; then
  for cand in /workspace/extract-loboforge-worker.sh /tmp/extract-loboforge-worker.sh; do
    if [[ -x "$cand" ]]; then EXTRACT="$cand"; break; fi
  done
fi
if [[ -z "$EXTRACT" ]]; then
  lf_fetch "${BASE}/agent/extract-loboforge-worker.sh" /tmp/extract-loboforge-worker.sh 2>/dev/null || true
  chmod +x /tmp/extract-loboforge-worker.sh 2>/dev/null || true
  EXTRACT="/tmp/extract-loboforge-worker.sh"
fi
if [[ -x "$EXTRACT" && -f /tmp/loboforge_worker.tar.gz ]]; then
  bash "$EXTRACT" /workspace /tmp/loboforge_worker.tar.gz "$PY"
elif [[ -f /tmp/loboforge_worker.tar.gz ]]; then
  # Fallback: flat tarball → loboforge_worker/
  TMP_EX="$(mktemp -d)"
  tar -xzf /tmp/loboforge_worker.tar.gz -C "$TMP_EX"
  rm -rf /workspace/loboforge_worker
  if [[ -f "$TMP_EX/loboforge_worker/__init__.py" ]]; then
    mv "$TMP_EX/loboforge_worker" /workspace/
  elif [[ -f "$TMP_EX/__init__.py" ]]; then
    mv "$TMP_EX" /workspace/loboforge_worker
  else
    echo "provision_ltx_native: bad worker tarball" >&2
    exit 1
  fi
  rm -rf "$TMP_EX"
  export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
  "$PY" -c "import loboforge_worker"
fi
[[ -f /tmp/loboforge_worker.tar.gz ]] && rm -f /tmp/loboforge_worker.tar.gz
lf_fetch "${BASE}/agent/loboforge_agent.py" /workspace/loboforge_agent.py
lf_fetch "${BASE}/agent/wd14_tagger.py" /workspace/wd14_tagger.py 2>/dev/null || true

export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

# Early fleet join — agent connects before multi-hour HF downloads (same as provision_gpu.sh phase_early_pool_join).
if ! "$PY" -m loboforge_worker provision-ltx-native --help 2>/dev/null | grep -q connect-only; then
  echo "ERROR: loboforge_worker missing provision-ltx-native — refresh worker tarball on server" | tee -a /workspace/provision.log
  exit 1
fi

"$PY" -m loboforge_worker provision-ltx-native --connect-only \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-ltx}" \
  --hf-token "$HF_TOKEN" \
  2>&1 | tee -a /workspace/provision.log

nohup "$PY" -m loboforge_worker provision-ltx-native --skip-agent-launch \
  --secret "$LOBO_SECRET" \
  --server "${LOBO_SERVER:-wss://www.loboforge.com}" \
  --base-url "$BASE" \
  --instance-id "$LOBO_INSTANCE_ID" \
  --label "${LOBO_LABEL:-loboforge-ltx}" \
  --hf-token "$HF_TOKEN" \
  >> /workspace/provision.log 2>&1 &
echo "background LTX downloads pid=$!" | tee -a /workspace/provision.log

BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
BASE="${BASE%/}"
lf_fetch "${BASE}/agent/ltx-agent-watchdog.sh" /workspace/ltx-agent-watchdog.sh 2>/dev/null || true
chmod +x /workspace/ltx-agent-watchdog.sh 2>/dev/null || true
service cron start 2>/dev/null || (command -v cron >/dev/null && pgrep -x cron >/dev/null || cron) 2>/dev/null || true
(crontab -l 2>/dev/null | grep -v ltx-agent-watchdog.sh || true
 echo '*/5 * * * * bash /workspace/ltx-agent-watchdog.sh >> /workspace/ltx-watchdog.log 2>&1') | crontab - 2>/dev/null || true
