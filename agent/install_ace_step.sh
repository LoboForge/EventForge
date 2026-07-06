#!/usr/bin/env bash
# Download ACE-Step checkpoint for music jobs on video boxes. Idempotent.
set -euo pipefail

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"

for root in /opt/workspace-internal/ComfyUI /workspace/ComfyUI /workspace/comfyui; do
  [[ -d "$root/models" ]] && MODELS="$root/models" && break
done
MODELS="${MODELS:-/workspace/ComfyUI/models}"
ACE_DEST="$MODELS/checkpoints/ace_step_v1_3.5b.safetensors"
MIN_BYTES=100000000

if [[ -f "$ACE_DEST" ]] && [[ "$(stat -c%s "$ACE_DEST" 2>/dev/null || echo 0)" -ge "$MIN_BYTES" ]]; then
  echo "ace_step already present at $ACE_DEST"
  exit 0
fi

export HF_TOKEN="${HF_TOKEN:-${HUGGINGFACE_HUB_TOKEN:-}}"
"$PY" -m pip install -q -U huggingface_hub 2>/dev/null || true
mkdir -p "$MODELS/checkpoints"
ACE_REPO="${ACE_REPO:-Comfy-Org/ACE-Step_ComfyUI_repackaged}"
ACE_INCLUDE="${ACE_INCLUDE:-all_in_one/ace_step_v1_3.5b.safetensors}"

echo "Downloading ACE-Step from $ACE_REPO..."
rm -rf /tmp/hf_ace
mkdir -p /tmp/hf_ace
hf download "$ACE_REPO" --include "$ACE_INCLUDE" --local-dir /tmp/hf_ace
found="$(find /tmp/hf_ace -name 'ace_step_v1_3.5b.safetensors' -type f | head -1)"
[[ -n "$found" && -f "$found" ]] || { echo "ACE-Step download missing checkpoint" >&2; exit 1; }
mv -f "$found" "$ACE_DEST"
rm -rf /tmp/hf_ace
echo "ACE-Step ready: $ACE_DEST"
