#!/usr/bin/env bash
# Durable external supervisor for EventForge WAN (Comfy) boxes — cron every 3 min.
#
# Incident (2026-07-21): three video boxes went idle because the ENTIRE
# `loboforge-agent` tmux session died (OOM-killed / tmux server crash). ComfyUI
# stayed up, the GPU sat idle, and nothing respawned the agent — the surgical
# supervisor loop only restarts the agent PROCESS, not a dead tmux session.
#
# This watchdog runs from cron (outside tmux), so it survives agent + tmux death
# and re-launches the supervisor via the surgical helper restart-wan-agent.sh.
# It also frees an obvious /tmp LoRA-staging disk bomb when the disk is critically
# full (never touches model checkpoints).
set -uo pipefail

SESSION=loboforge-agent
WD_LOG=/workspace/wan-supervisor-watchdog.log
RESTART=/workspace/restart-wan-agent.sh
AGENT_PAT='loboforge_agent_eventforge.py'

mkdir -p /workspace
cd /workspace
[[ -f /workspace/.loboforge-env ]] && set -a && . /workspace/.loboforge-env && set +a

log() { echo "[$(date -Is)] $*" >> "$WD_LOG"; }

EF_BASE="${EVENT_FORGE_URL:-https://eventforge.loboforge.com}"; EF_BASE="${EF_BASE%/}"
BASE="${LOBO_BASE_URL:-https://www.loboforge.com}"
case "$(printf '%s' "$BASE" | tr '[:upper:]' '[:lower:]')" in
  *eventforge.loboforge.com*) BASE="https://www.loboforge.com" ;;
esac
BASE="${BASE%/}"
LF_UA="LoboForge-Worker/1.1"

lf_fetch() {
  local url="$1" dest="$2"
  curl -fsSL -A "$LF_UA" "$url" -o "$dest" 2>/dev/null && return 0
  local py="/venv/main/bin/python3"; [[ -x "$py" ]] || py="$(command -v python3)"
  [[ -n "$py" ]] || return 1
  LF_URL="$url" LF_DEST="$dest" "$py" - <<'PY' 2>/dev/null
import os, urllib.request
req = urllib.request.Request(os.environ["LF_URL"], headers={"User-Agent": "LoboForge-Worker/1.1"})
open(os.environ["LF_DEST"], "wb").write(urllib.request.urlopen(req, timeout=120).read())
PY
}

# This watchdog is for Comfy WAN boxes only. Native LTX/Wan boxes have their own
# (ltx|wan)-agent-watchdog.sh; do not fight them.
if [[ "${LOBO_EXECUTOR:-}" == "native" || "${LOBO_LABEL:-}" == *ltx* || "${LOBO_MODE:-}" == *native* ]]; then
  exit 0
fi

# Vast images often reboot without starting cron — heal that every run.
if command -v service >/dev/null 2>&1; then
  service cron start 2>/dev/null || true
elif command -v cron >/dev/null 2>&1; then
  pgrep -x cron >/dev/null 2>&1 || cron 2>/dev/null || true
fi

# Self-install: keep our own copy fresh and re-arm the cron entry idempotently.
if [[ ! -x /workspace/wan-supervisor-watchdog.sh ]]; then
  for wd_url in "${EF_BASE}/agent/wan-supervisor-watchdog.sh" "${BASE}/agent/wan-supervisor-watchdog.sh"; do
    lf_fetch "$wd_url" /workspace/wan-supervisor-watchdog.sh && break
  done
  chmod +x /workspace/wan-supervisor-watchdog.sh 2>/dev/null || true
fi
(crontab -l 2>/dev/null | grep -v 'wan-supervisor-watchdog.sh' || true
 echo '*/3 * * * * bash /workspace/wan-supervisor-watchdog.sh >> /workspace/wan-supervisor-watchdog.log 2>&1') | crontab - 2>/dev/null || true

# Ensure the surgical restart helper is present (fetch if missing).
if [[ ! -f "$RESTART" ]]; then
  for r_url in "${EF_BASE}/agent/restart-wan-agent.sh" "${BASE}/agent/restart-wan-agent.sh"; do
    lf_fetch "$r_url" "$RESTART" && break
  done
  chmod +x "$RESTART" 2>/dev/null || true
fi

# Disk guard: if the container disk is critically full, drop the obvious LoRA
# staging bomb (/tmp/lora_in) that can accumulate mid-download. NEVER touch model
# checkpoints (/workspace/wan-models, comfy models, etc.).
disk_use=$(df -P / 2>/dev/null | awk 'NR==2{gsub("%","",$5); print $5+0}')
avail_mb=$(df -Pm / 2>/dev/null | awk 'NR==2{print $4+0}')
if { [[ -n "${disk_use:-}" && "$disk_use" -ge 97 ]] || [[ -n "${avail_mb:-}" && "$avail_mb" -lt 2048 ]]; }; then
  for bomb in /tmp/lora_in /tmp/loboforge-lora_in; do
    if [[ -d "$bomb" ]]; then
      sz=$(du -sh "$bomb" 2>/dev/null | awk '{print $1}')
      rm -rf "${bomb:?}/"* 2>/dev/null || true
      log "disk critical (use=${disk_use}% avail=${avail_mb}MB) — cleared ${bomb} (${sz})"
    fi
  done
fi

restart_agent() {
  local reason="$1"
  log "supervisor DOWN ($reason) — invoking restart-wan-agent.sh"
  if [[ -f "$RESTART" ]]; then
    bash "$RESTART" >> "$WD_LOG" 2>&1 && { log "restart OK"; return 0; }
    log "restart-wan-agent.sh failed"
  else
    log "restart-wan-agent.sh missing and could not be fetched"
  fi
  return 1
}

# Primary check: whole tmux supervisor session gone (the reported incident).
if ! tmux has-session -t "$SESSION" 2>/dev/null; then
  restart_agent "tmux session '$SESSION' missing"
  exit 0
fi

# Secondary check: session exists but agent process is gone. The supervisor loop
# relaunches a crashed agent within ~5s, so sample twice to avoid flapping during
# a legitimate restart window. Only a sustained absence means a wedged loop.
if ! pgrep -f "$AGENT_PAT" >/dev/null 2>&1; then
  sleep 25
  if ! pgrep -f "$AGENT_PAT" >/dev/null 2>&1; then
    tmux kill-session -t "$SESSION" 2>/dev/null || true
    restart_agent "session up but no agent PID for >25s (wedged supervisor)"
  fi
fi
exit 0
