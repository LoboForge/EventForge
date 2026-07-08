#!/usr/bin/env bash
# Shared worker health check — source from start_worker.sh and watchdog.sh
# Exit 0 = healthy, 1 = needs restart
joycaption_worker_healthy() {
  local log="${1:-/workspace/joycaption/worker.log}"
  local max_job_sec="${2:-120}"
  local max_idle_sec="${3:-300}"
  local max_load_sec="${4:-900}"

  pgrep -f '[/]joycaption_worker.py' >/dev/null 2>&1 || return 1
  test -f "$log" || return 1

  local age=$(( $(date +%s) - $(stat -c %Y "$log" 2>/dev/null || echo 0) ))
  local last
  last=$(grep -E '\[worker\] (job|done|registered|reconnecting|disconnected|failed)' "$log" 2>/dev/null | tail -1)

  if [[ "$last" == *"[worker] done"* ]]; then
    [ "$age" -le "$max_idle_sec" ] && return 0
    return 1
  fi
  if [[ "$last" == *"[worker] job"* ]]; then
    [ "$age" -le "$max_job_sec" ] && return 0
    return 1
  fi
  if [[ "$last" == *"[worker] registered"* ]]; then
    [ "$age" -le 180 ] && return 0
    return 1
  fi
  if [[ "$last" == *"[worker] reconnecting"* ]] || [[ "$last" == *"[worker] disconnected"* ]]; then
    [ "$age" -le 60 ] && return 0
    return 1
  fi
  if tail -5 "$log" 2>/dev/null | grep -qE 'Loading JoyCaption model|Fetching [0-9]+ files'; then
    [ "$age" -le "$max_load_sec" ] && return 0
    return 1
  fi
  [ "$age" -le 60 ] && return 0
  return 1
}

joycaption_ef_worker_healthy() {
  local log="${1:-/workspace/joycaption/worker.log}"
  local max_job_sec="${2:-180}"
  local max_idle_sec="${3:-300}"
  local max_load_sec="${4:-900}"

  pgrep -f '[/]joycaption_eventforge_worker.py' >/dev/null 2>&1 || return 1
  test -f "$log" || return 1

  local age=$(( $(date +%s) - $(stat -c %Y "$log" 2>/dev/null || echo 0) ))
  local last
  last=$(grep -E '\[ef-worker\] (Joycaption job|EventForge claimed|JoyCaption model ready|loop error)' "$log" 2>/dev/null | tail -1)

  if [[ "$last" == *"Joycaption job"* && "$last" == *"complete"* ]]; then
    [ "$age" -le "$max_idle_sec" ] && return 0
    return 1
  fi
  if [[ "$last" == *"EventForge claimed"* ]] || [[ "$last" == *"Joycaption job"* ]]; then
    [ "$age" -le "$max_job_sec" ] && return 0
    return 1
  fi
  if [[ "$last" == *"JoyCaption model ready"* ]]; then
    [ "$age" -le "$max_load_sec" ] && return 0
    return 1
  fi
  if tail -8 "$log" 2>/dev/null | grep -qE 'Loading JoyCaption model|Fetching [0-9]+ files|JoyCaption model ready'; then
    [ "$age" -le "$max_load_sec" ] && return 0
    return 1
  fi
  [ "$age" -le 120 ] && return 0
  return 1
}
