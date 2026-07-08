#!/usr/bin/env bash
# Vast.ai bootstrap — JoyCaption venv inherits Comfy CUDA torch (do NOT pip install cu130 torch)
set -euo pipefail

mkdir -p /root/.ssh /workspace/joycaption /workspace/jobs
DEVKEY_LINE='ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIE3GcWhqotDSyTJVMf+PfE8rACP9OZryO+jrMrlMzrok dev@loboforge.com'
grep -qF 'dev@loboforge.com' /root/.ssh/authorized_keys 2>/dev/null || echo "$DEVKEY_LINE" >> /root/.ssh/authorized_keys
chmod 700 /root/.ssh 2>/dev/null || true
chmod 600 /root/.ssh/authorized_keys 2>/dev/null || true

SEED="/venv/main/bin/python3"
[[ -x "$SEED" ]] || SEED="$(command -v python3)"

VENV="/workspace/joycaption/venv"
PY="$VENV/bin/python3"

if [[ ! -x "$PY" ]] || ! "$PY" -c "import torch; exit(0 if torch.cuda.is_available() else 1)" 2>/dev/null; then
  rm -rf "$VENV"
  "$SEED" -m venv --system-site-packages "$VENV"
fi

"$PY" -m pip install -q -U pip
# Never reinstall torch — use Comfy image CUDA build via --system-site-packages
"$PY" -m pip install -q \
  "transformers>=5.9" "bitsandbytes>=0.45.0" accelerate \
  pillow tqdm huggingface_hub

"$PY" -c "import torch; assert torch.cuda.is_available(), 'CUDA not available — wrong torch build'"
cap=$("$PY" -c "import torch; print(torch.cuda.get_device_capability(0)[0] if torch.cuda.is_available() else 99)" 2>/dev/null || echo 99)
if [[ "$cap" -lt 7 ]]; then
  echo "ERROR: GPU compute capability ${cap}.0 too old (need >=7.0 / V100+). PyTorch cu128 cannot run Pascal P100." >&2
  exit 1
fi

if ! curl -sf --max-time 20 -o /dev/null https://huggingface.co; then
  echo "ERROR: no outbound network to huggingface.co (cannot download JoyCaption model)" >&2
  exit 1
fi

DEFAULT_HF_TOKEN=''
export HF_TOKEN="${HF_TOKEN:-${HUGGINGFACE_HUB_TOKEN:-$DEFAULT_HF_TOKEN}}"
export HUGGINGFACE_HUB_TOKEN="${HF_TOKEN:-}"
if [[ -z "${HF_TOKEN}" ]]; then
  echo "WARN: HF_TOKEN not set — JoyCaption model download may fail" >&2
fi
export HF_HOME="/workspace/joycaption/hf_cache"
export TRANSFORMERS_CACHE="$HF_HOME"
mkdir -p "$HF_HOME"

# Pre-download JoyCaption weights during bootstrap so first job does not cold-start HF.
if [[ ! -f "$HF_HOME/models--fancyfeast--llama-joycaption-beta-one-hf-llava/refs/main" ]] \
    && ! find "$HF_HOME" -maxdepth 3 -type d -name 'models--fancyfeast--llama-joycaption-beta-one-hf-llava' 2>/dev/null | grep -q .; then
  echo "[$(date -Is)] Pre-downloading JoyCaption model (HF_TOKEN set)…" >> /workspace/joycaption/bootstrap.log
  "$PY" - <<'PY' >> /workspace/joycaption/bootstrap.log 2>&1 || echo "WARN: JoyCaption model pre-download failed" >> /workspace/joycaption/bootstrap.log
import os
from huggingface_hub import snapshot_download
snapshot_download(
    "fancyfeast/llama-joycaption-beta-one-hf-llava",
    token=os.environ.get("HF_TOKEN") or None,
    cache_dir=os.environ.get("HF_HOME"),
)
print("JoyCaption model cache ready")
PY
fi

touch /workspace/joycaption/.bootstrapped
echo "[$(date -Is)] JoyCaption venv ready py=$PY cuda=$( $PY -c 'import torch; print(torch.__version__)' )" >> /workspace/joycaption/bootstrap.log
