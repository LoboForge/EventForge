#!/usr/bin/env bash
# Local/CI gate: no large binaries in git, backend tests + frontend build.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# Soft cap for intentional small artifacts (e.g. agent/loboforge_worker.tar.gz ~175KB).
# LoRAs/checkpoints are multi-MB–GB and must never enter the repo — use S3 / POST /v1/assets/loras.
MAX_BYTES=$((1024 * 1024)) # 1 MiB

echo "▶ refuse large / weight binaries in git"
bad=0
while IFS= read -r path; do
  [[ -z "$path" ]] && continue
  case "$path" in
    *.safetensors|*.ckpt|*.pt|*.pth|*.onnx|*.gguf|*.ggml|*.engine|*.npz|\
    *.mp4|*.webm|*.zip|*.7z|*.rar|\
    .tmp-loras/*|*/.tmp-loras/*|\
    *__pycache__/*|*.pyc)
      echo "  FORBIDDEN path tracked: $path" >&2
      bad=1
      continue
      ;;
  esac
  if [[ -f "$path" ]]; then
    size=$(wc -c <"$path" | tr -d ' ')
    if (( size > MAX_BYTES )); then
      echo "  FORBIDDEN oversized tracked file (${size} bytes > ${MAX_BYTES}): $path" >&2
      bad=1
    fi
  fi
done < <(git ls-files -z | tr '\0' '\n')

if (( bad )); then
  echo "Large binaries / model weights must not be in git. Upload LoRAs via POST /v1/assets/loras (S3)." >&2
  exit 1
fi

echo "▶ dotnet test (Release)"
dotnet test EventForge.Tests/EventForge.Tests.csproj --configuration Release --verbosity normal

echo "▶ web build"
(
  cd web
  npm ci
  node node_modules/typescript/bin/tsc -b
  node node_modules/vite/bin/vite.js build
)

echo "✓ CI checks passed"
