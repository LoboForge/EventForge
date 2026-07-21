#!/usr/bin/env bash
# Requeue failed wan jobs when additional wan-native boxes start taking work.
# Usage: bash scripts/watch-wan-requeue.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"

OPS_KEY="${EVENT_FORGE_OPS_KEY:?}"
EF_URL="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
EF_URL="${EF_URL%/}"

declare -A REQUEUED_FOR=()
REQUEUED_COUNT=0
BOXES=(
  "44566437:ssh2.vast.ai:16436"
  "44566445:ssh8.vast.ai:16444"
  "44566756:ssh2.vast.ai:16756"
  "44566757:ssh2.vast.ai:16756"
)

requeue_failed() {
  local reason="$1"
  local resp
  resp=$(curl -sS -X POST -H "X-EventForge-Ops-Key: $OPS_KEY" -H "Content-Type: application/json" -d '{}' \
    "$EF_URL/v1/ops/jobs/requeue-failed" 2>/dev/null || echo '{}')
  local n
  n=$(python3 -c "import json,sys; print(json.load(sys.stdin).get('requeued',0))" <<<"$resp" 2>/dev/null || echo 0)
  echo "[$(date -Is)] requeue ($reason): $n jobs — $resp" | head -c 500
  echo
}

box_taking_jobs() {
  local id="$1" host="$2" port="$3"
  local fleet_busy=""
  fleet_busy=$(curl -sS -H "X-EventForge-Ops-Key: $OPS_KEY" "$EF_URL/v1/ops/fleet" 2>/dev/null | \
    python3 -c "
import json,sys
d=json.load(sys.stdin)
hn='loboforge-wan-native-$id'
for w in d.get('workers',[]):
    if w.get('hostname')==hn:
        cr=w.get('claim_ready_capabilities') or []
        print('1' if (w.get('busy') or ('wan' in cr)) else '0')
        break
else:
    print('0')
" 2>/dev/null || echo 0)

  if [[ "$fleet_busy" == "1" ]]; then
    echo taking
    return 0
  fi

  ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=12 -o BatchMode=yes "root@${host}" \
    'test -f /workspace/wan-models/layout.json && pgrep -f loboforge_agent_eventforge >/dev/null && pgrep -f provision-wan-native >/dev/null; echo ready=$([ -f /workspace/wan-models/layout.json ] && echo yes || echo no); pgrep -cf loboforge_agent_eventforge || echo 0' \
    2>/dev/null | grep -q 'ready=yes' && [[ "$(ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=8 -o BatchMode=yes "root@${host}" 'pgrep -cf loboforge_agent_eventforge || echo 0' 2>/dev/null)" -gt 0 ]] && \
    curl -sS -H "X-EventForge-Ops-Key: $OPS_KEY" "$EF_URL/v1/ops/jobs/active" 2>/dev/null | \
    python3 -c "import json,sys; d=json.load(sys.stdin); hn='loboforge-wan-native-$id'; print('1' if any(j.get('hostname')==hn for j in d.get('jobs',[])) else '0')" 2>/dev/null | grep -q 1 && echo taking
}

echo "Watching wan-native boxes for job activity (Ctrl+C to stop)..."
while true; do
  for spec in "${BOXES[@]}"; do
    id=${spec%%:*}; rest=${spec#*:}; host=${rest%%:*}; port=${rest##*:}
    if [[ -n "${REQUEUED_FOR[$id]:-}" ]]; then
      continue
    fi
    if curl -sS -H "X-EventForge-Ops-Key: $OPS_KEY" "$EF_URL/v1/ops/fleet" 2>/dev/null | python3 -c "
import json,sys
id='$id'
d=json.load(sys.stdin)
hn=f'loboforge-wan-native-{id}'
for w in d.get('workers',[]):
    if w.get('hostname')==hn and (w.get('busy') or 'wan' in (w.get('claim_ready_capabilities') or [])):
        sys.exit(0)
sys.exit(1)
" 2>/dev/null; then
      REQUEUED_FOR[$id]=1
      REQUEUED_COUNT=$((REQUEUED_COUNT + 1))
      requeue_failed "box $id active in fleet"
      continue
    fi
    if curl -sS -H "X-EventForge-Ops-Key: $OPS_KEY" "$EF_URL/v1/ops/jobs/active" 2>/dev/null | python3 -c "
import json,sys
hn='loboforge-wan-native-$id'
d=json.load(sys.stdin)
sys.exit(0 if any(j.get('hostname')==hn for j in d.get('jobs',[])) else 1)
" 2>/dev/null; then
      REQUEUED_FOR[$id]=1
      REQUEUED_COUNT=$((REQUEUED_COUNT + 1))
      requeue_failed "box $id has active job"
    fi
  done
  if [[ "$REQUEUED_COUNT" -ge "${#BOXES[@]}" ]]; then
    echo "[$(date -Is)] all target boxes triggered requeue — done"
    exit 0
  fi
  sleep 120
done
