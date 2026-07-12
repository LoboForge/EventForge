#!/usr/bin/env bash
# Re-run Wan 2.2 native provision on running loboforge-wan-native Vast boxes:
# worker tarball, native env, gdown, sync-loras (video), background model downloads, watchdog.
#
# Usage:
#   bash scripts/patch-wan-native-fleet.sh
#   INSTANCE_IDS="44182904 44566437" bash scripts/patch-wan-native-fleet.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"

VAST_KEY="${VAST_API_KEY:?Set VAST_API_KEY or EventForge.VastAi.ApiKey in secrets.local.json}"
LOBO_SECRET="${LOBO_SECRET:?Set LoboForge.WorkersSecret in secrets.local.json}"
EVENT_FORGE_URL="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:?Set EventForge.WorkerKey}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
export INSTANCE_FILTER="${INSTANCE_IDS:-}"

mapfile -t TARGETS < <(curl -s "https://console.vast.ai/api/v0/instances/" \
  -H "Authorization: Bearer $VAST_KEY" | python3 -c "
import json, os, sys
want = set((os.environ.get('INSTANCE_FILTER') or '').split())
for i in json.load(sys.stdin).get('instances', []):
    iid = str(i['id'])
    lab = (i.get('label') or '').lower()
    if i.get('actual_status') != 'running':
        continue
    if want and iid not in want:
        continue
    if not want and 'wan-native' not in lab:
        continue
    print(i['id'], i.get('ssh_host'), i.get('ssh_port'), i.get('label'))
")

if [[ ${#TARGETS[@]} -eq 0 ]]; then
  echo "No running loboforge-wan-native instances (filter=${INSTANCE_FILTER:-auto})"
  exit 1
fi

echo "Patching ${#TARGETS[@]} wan-native box(es)..."

patch_one() {
  local id="$1" host="$2" port="$3" label="$4"
  local hn="loboforge-wan-native-${id}"
  echo ""
  echo "▶ $id ($label) $host:$port hostname=$hn"

  ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=45 "root@${host}" \
    "LOBO_SECRET=$(printf '%q' "$LOBO_SECRET")" \
    "LOBO_BASE_URL=$(printf '%q' "$LOBO_BASE_URL")" \
    "LOBO_SERVER=$(printf '%q' "${LOBO_SERVER:-wss://www.loboforge.com}")" \
    "EVENT_FORGE_URL=$(printf '%q' "$EVENT_FORGE_URL")" \
    "EVENT_FORGE_WORKER_KEY=$(printf '%q' "$EVENT_FORGE_WORKER_KEY")" \
    "LOBO_INSTANCE_ID=$(printf '%q' "$id")" \
    "LOBO_LABEL=$(printf '%q' "$label")" \
    "HN=$(printf '%q' "$hn")" \
    "EF_BASE=$(printf '%q' "$EVENT_FORGE_URL")" \
    bash -s <<'REMOTE'
set -euo pipefail
mkdir -p /workspace
cd /workspace
PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"
LF_UA="LoboForge-Worker/1.1"

tmux kill-session -t loboforge-agent 2>/dev/null || true
pkill -9 -f 'loboforge_agent_eventforge' 2>/dev/null || true
sleep 2

for url in "${EF_BASE}/agent/provision_wan_native.sh" "${LOBO_BASE_URL}/agent/provision_wan_native.sh"; do
  if curl -fsSL -A "$LF_UA" "$url" -o /workspace/provision_wan_native.sh; then break; fi
done
chmod +x /workspace/provision_wan_native.sh

export LOBO_SECRET="$LOBO_SECRET"
export LOBO_BASE_URL="$LOBO_BASE_URL"
export LOBO_SERVER="$LOBO_SERVER"
export EVENT_FORGE_URL="$EVENT_FORGE_URL"
export EVENT_FORGE_WORKER_KEY="$EVENT_FORGE_WORKER_KEY"
export LOBO_INSTANCE_ID="$LOBO_INSTANCE_ID"
export CONTAINER_ID="$LOBO_INSTANCE_ID"
export LOBO_LABEL="$LOBO_LABEL"
export LOBO_GEN_QUEUE=eventforge
export LOBO_EXECUTOR=native
export LOBO_SKIP_COMFY=1
export LOBO_WAN=1
export LOBO_LTX23=0
export LOBO_MUSIC=0
export MODE=wan-native
export LOBO_MODE=wan-native
export WAN_MODEL_ROOT=/workspace/wan-models
export FORGE_QUEUE_CAPABILITY=wan
export HN="$HN"

# Re-run native onstart (idempotent: fetches tarball, sync-loras, starts background downloads).
bash /workspace/provision_wan_native.sh 2>&1 | tee -a /workspace/provision-repatch.log | tail -20

echo "--- post-patch ---"
grep -E 'LOBO_EXECUTOR|MODE|EVENT_FORGE_URL|WAN_MODEL_ROOT' /workspace/.loboforge-env 2>/dev/null | head -6 || true
LORA_DIR=/opt/workspace-internal/ComfyUI/models/loras
[[ -d /workspace/ComfyUI/models/loras ]] && LORA_DIR=/workspace/ComfyUI/models/loras
echo "loras=$(ls "$LORA_DIR" 2>/dev/null | grep -v put_loras | wc -l) dir=$LORA_DIR"
if [[ -f /workspace/wan-models/layout.json ]]; then
  echo "wan-models=$(du -sh /workspace/wan-models | awk '{print $1}') layout=ok"
else
  echo "wan-models=$(du -sh /workspace/wan-models 2>/dev/null | awk '{print $1}' || echo missing) layout=pending"
fi
pgrep -af 'provision-wan-native|loboforge_agent' | head -4 || true
tail -2 /workspace/lora-sync.log 2>/dev/null || true
REMOTE
}

for line in "${TARGETS[@]}"; do
  read -r id host port label <<< "$line"
  patch_one "$id" "$host" "$port" "$label" || echo "  WARN: patch failed on $id"
done

echo ""
echo "Done. Native models download in background; watchdog starts agent when layout.json is ready."
