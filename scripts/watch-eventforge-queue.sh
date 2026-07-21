#!/usr/bin/env bash
# Poll EventForge ops queue + fleet; alert on failed jobs or idle wan workers.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"

BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"
OPS_KEY="${EVENT_FORGE_OPS_KEY:?Set EventForge.OpsKey}"
INTERVAL="${1:-120}"
LOG="${EVENTFORGE_WATCH_LOG:-/tmp/eventforge-queue-watch.log}"
MAX_ROUNDS="${EVENTFORGE_WATCH_ROUNDS:-0}"

hdr() { echo "=== EventForge watch @ $(date -Is) ===" | tee -a "$LOG"; }

poll_once() {
  hdr
  local queue fleet
  queue=$(curl -sf --max-time 30 "$BASE/v1/ops/queue" -H "X-EventForge-Ops-Key: $OPS_KEY") || {
    echo "queue poll FAILED" | tee -a "$LOG"
    return 1
  }
  fleet=$(curl -sf --max-time 30 "$BASE/v1/ops/fleet" -H "X-EventForge-Ops-Key: $OPS_KEY") || {
    echo "fleet poll FAILED" | tee -a "$LOG"
    return 1
  }

  python3 - "$queue" "$fleet" <<'PY' | tee -a "$LOG"
import json, sys
queue, fleet = json.loads(sys.argv[1]), json.loads(sys.argv[2])
print(
    f"queue total={queue.get('jobs_total')} "
    f"queued={queue.get('jobs_queued')} "
    f"in_progress={queue.get('jobs_in_progress')} "
    f"failed={queue.get('jobs_failed')} "
    f"completed={queue.get('jobs_completed')}"
)
for c in queue.get("by_capability") or []:
    if c.get("failed") or c.get("in_progress"):
        print(f"  cap {c.get('capability')}: queued={c.get('queued')} ip={c.get('in_progress')} failed={c.get('failed')}")

alerts = []
for w in fleet.get("workers") or []:
    hn = w.get("hostname") or "?"
    ready = w.get("claimReadyCapabilities") or []
    busy = w.get("busy")
    job = (w.get("currentJobUuid") or "")[:8]
    stale = w.get("checkInStale")
    line = (
        f"  {hn}: state={w.get('state')} ready={ready} "
        f"busy={busy} job={job or '-'} "
        f"done={w.get('jobsCompleted',0)} fail={w.get('jobsFailed',0)} stale={stale}"
    )
    print(line)
    if "wan" in ready or "wan" in (w.get("capabilities") or []):
        if not busy and not ready and not stale:
            alerts.append(f"ALERT wan worker {hn} not claim-ready")
    if stale:
        alerts.append(f"ALERT stale check-in {hn}")

if queue.get("jobs_failed", 0) > 0:
    alerts.append(f"ALERT {queue['jobs_failed']} failed jobs — requeue may be needed")
if alerts:
    print("---")
    for a in alerts:
        print(a)
PY
}

round=0
echo "Watching $BASE every ${INTERVAL}s → $LOG (rounds=${MAX_ROUNDS:-∞})"
while true; do
  poll_once || true
  round=$((round + 1))
  [[ "$MAX_ROUNDS" -gt 0 && "$round" -ge "$MAX_ROUNDS" ]] && break
  sleep "$INTERVAL"
done
