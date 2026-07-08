#!/usr/bin/env bash
# On-box auto-heal loop — restarts EventForge worker if hung/disconnected.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=vast_joycaption_health.sh
source "$DIR/vast_joycaption_health.sh"

LOG="${JOYCAPTION_WORKER_LOG:-/workspace/joycaption/worker.log}"
WLOG="/workspace/joycaption/watchdog.log"
START="${JOYCAPTION_START_SCRIPT:-/workspace/joycaption/start_worker.sh}"
CHECK_SEC="${JOYCAPTION_WATCHDOG_SEC:-30}"

log() { echo "[$(date -Is)] [watchdog] $*" | tee -a "$WLOG"; }

restart_worker() {
  log "unhealthy — restarting EventForge worker + model server"
  pkill -f '[/]joycaption_eventforge_worker.py' 2>/dev/null || true
  pkill -f '[/]joycaption_server.py' 2>/dev/null || true
  sleep 2
  JOYCAPTION_SKIP_WATCHDOG=1 bash "$START" >>"$WLOG" 2>&1 || log "start_worker failed"
}

log "watchdog started (check every ${CHECK_SEC}s)"
while true; do
  sleep "$CHECK_SEC"
  if joycaption_ef_worker_healthy "$LOG"; then
    continue
  fi
  restart_worker
done
