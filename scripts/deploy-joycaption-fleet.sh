#!/usr/bin/env bash
# Push batched JoyCaption EventForge worker bundle to running joycaption Vast boxes.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUNDLE="${ROOT}/agent/joycaption"
KEY="${VAST_SSH_KEY:-${HOME}/.ssh/vast_tmp}"
KH="${TMPDIR:-/tmp}/vast_known_hosts"
SSH_BASE=(-i "$KEY" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile="$KH" -o ConnectTimeout=20 -o BatchMode=yes)

SECRETS="${SECRETS_JSON:-$ROOT/secrets.local.json}"
if [[ ! -f "$SECRETS" ]]; then
  echo "Missing secrets: $SECRETS" >&2
  exit 1
fi

EF_URL=$(python3 -c "import json; print(json.load(open('$SECRETS'))['EventForge'].get('PublicUrl','https://eventforge.loboforge.com'))")
EF_WORKER_KEY=$(python3 -c "import json; print(json.load(open('$SECRETS'))['EventForge']['WorkerKey'])")
VAST_KEY=$(python3 -c "import json; print(json.load(open('$SECRETS'))['EventForge']['VastAi']['ApiKey'])")

deploy_one() {
  local id="$1" host="$2" port="$3" gpu="$4"
  local SSH=(ssh -p "$port" "${SSH_BASE[@]}" "root@${host}")
  local RSYNC_E="ssh -p ${port} ${SSH_BASE[*]}"

  echo "=== #$id ($gpu @ ${host}:${port}) ==="
  if ! "${SSH[@]}" "echo ok" >/dev/null 2>&1; then
    echo "  SKIP: SSH unavailable"
    return 0
  fi

  "${SSH[@]}" "mkdir -p /workspace/joycaption /workspace/jobs /workspace/joycaption/hf_cache" || true

  for pair in \
    "vast_joycaption_onstart.sh:/workspace/joycaption/bootstrap.sh" \
    "vast_joycaption_healthcheck.sh:/workspace/joycaption/healthcheck.sh" \
    "joycaption_server.py:/workspace/joycaption/joycaption_server.py" \
    "joycaption_eventforge_worker.py:/workspace/joycaption/joycaption_eventforge_worker.py" \
    "joycaption_prompt.json:/workspace/joycaption/joycaption_prompt.json" \
    "joycaption_cli.py:/workspace/joycaption/joycaption_cli.py" \
    "vast_joycaption_eventforge_worker.sh:/workspace/joycaption/start_worker.sh" \
    "vast_joycaption_health.sh:/workspace/joycaption/vast_joycaption_health.sh" \
    "vast_joycaption_watchdog.sh:/workspace/joycaption/watchdog.sh"; do
    local src="${pair%%:*}" dst="${pair##*:}"
    rsync -az --timeout=120 -e "$RSYNC_E" "${BUNDLE}/${src}" "root@${host}:${dst}" || { echo "  FAIL rsync $src"; return 1; }
  done

  "${SSH[@]}" "chmod +x /workspace/joycaption/bootstrap.sh /workspace/joycaption/healthcheck.sh /workspace/joycaption/start_worker.sh /workspace/joycaption/watchdog.sh"

  if ! "${SSH[@]}" "test -x /workspace/joycaption/venv/bin/python3 && test -f /workspace/joycaption/.bootstrapped"; then
    echo "  bootstrapping venv..."
    "${SSH[@]}" "bash /workspace/joycaption/bootstrap.sh 2>&1 | tail -8" || { echo "  FAIL bootstrap"; return 1; }
  fi

  if out=$("${SSH[@]}" "bash /workspace/joycaption/healthcheck.sh 2>&1"); then
    echo "  OK: $out"
  else
    echo "  FAIL healthcheck: $out"
    return 1
  fi

  echo "  starting EventForge batched worker…"
  "${SSH[@]}" "EVENT_FORGE_URL='${EF_URL}' EVENT_FORGE_WORKER_KEY='${EF_WORKER_KEY}' bash /workspace/joycaption/start_worker.sh" || true
}

export -f deploy_one
export BUNDLE KEY KH SSH_BASE EF_URL EF_WORKER_KEY

mapfile -t lines < <(curl -sS "https://console.vast.ai/api/v0/instances/?api_key=${VAST_KEY}" | python3 -c "
import json,sys
for i in json.load(sys.stdin).get('instances') or []:
    if i.get('label')=='joycaption' and i.get('actual_status')=='running':
        print(i['id'], i['ssh_host'], i['ssh_port'], i.get('gpu_name','?'))
")

echo "Deploying batched JoyCaption bundle to ${#lines[@]} worker(s)…"
for line in "${lines[@]}"; do
  read -r id host port gpu <<< "$line"
  deploy_one "$id" "$host" "$port" "$gpu" &
done
wait
echo "JoyCaption fleet deploy complete."
