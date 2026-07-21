#!/usr/bin/env bash
# Monitor wan-native Vast boxes: disk, Comfy, agent, model readiness.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=/dev/null
source "$ROOT/scripts/lib/secrets.sh"

KEY="${VAST_SSH_KEY:-${HOME}/.ssh/vast_tmp}"
LOG="${WAN_WATCH_LOG:-/tmp/wan-native-watch.log}"

ids_from_vast() {
  curl -fsS -H "Authorization: Bearer $VAST_API_KEY" \
    "https://console.vast.ai/api/v0/instances/" \
    | python3 -c "
import json,sys
for x in json.load(sys.stdin).get('instances') or []:
    if 'wan-native' in str(x.get('label') or '').lower():
        print(x['id'], x.get('ssh_host',''), x.get('ssh_port',''), x.get('gpu_name',''), x.get('actual_status',''))
"
}

check_box() {
  local id="$1" host="$2" port="$3" gpu="$4" vast_status="$5"
  echo "=== $(date -Is) id=$id $gpu status=$vast_status ===" | tee -a "$LOG"
  if ! ssh -i "$KEY" -p "$port" -o IdentitiesOnly=yes -o StrictHostKeyChecking=no \
      -o BatchMode=yes -o ConnectTimeout=15 -o UserKnownHostsFile=/dev/null \
      "root@$host" 'bash -s' <<'REMOTE' 2>/dev/null | tee -a "$LOG"
set +e
disk=$(df -P / | awk 'NR==2{printf "%s used %s free pct=%s", $3, $4, $5}')
echo "disk $disk"
comfy=$(pgrep -cf 'ComfyUI|comfyui|main.py.*8188' 2>/dev/null || true)
comfy=${comfy:-0}
ports=$(ss -tlnp 2>/dev/null | grep -cE ':8188|:18188' || true)
ports=${ports:-0}
echo "comfy_procs=$comfy comfy_ports=$ports"
if [[ "$comfy" -gt 0 || "$ports" -gt 0 ]]; then
  echo "ALERT: Comfy detected — killing"
  pkill -f 'ComfyUI|comfyui|main.py.*8188' 2>/dev/null || true
fi
agent=$(pgrep -cf loboforge_agent_eventforge || echo 0)
echo "agent_procs=$agent"
grep -E 'LOBO_EXECUTOR|LOBO_SKIP_COMFY|MODE=' /workspace/.loboforge-env 2>/dev/null | head -3
export PYTHONPATH=/workspace
ready=$(/venv/main/bin/python3 -c "from loboforge_worker.inference.wan.paths import i2v_ready; print(i2v_ready())" 2>/dev/null || echo unknown)
echo "i2v_ready=$ready"
if [[ "$ready" == "True" && "$agent" == "0" ]]; then
  echo "ALERT: models ready but no agent — starting"
  bash /workspace/start-wan-agent.sh 2>/dev/null || true
fi
pct=$(df -P / | awk 'NR==2{gsub(/%/,""); print $5}')
if [[ "$pct" -ge 88 ]]; then
  echo "ALERT: disk ${pct}% — cleaning cache"
  /venv/main/bin/python3 -c "import sys; sys.path.insert(0,'/workspace'); from loboforge_worker.inference.wan.paths import i2v_ready; sys.exit(0 if i2v_ready() else 1)" 2>/dev/null \
    && rm -rf /workspace/wan-models/Wan2.2-I2V-A14B/.cache /tmp/lobo-ref-images 2>/dev/null
  find /workspace/wan-models -name '*.part' -delete 2>/dev/null || true
  echo "disk_after $(df -P / | awk 'NR==2{print $5, $4}')"
fi
REMOTE
  then
    echo "SSH_FAIL id=$id $host:$port" | tee -a "$LOG"
  fi
}

main() {
  while read -r id host port gpu status; do
    [[ -n "$id" && -n "$host" && -n "$port" ]] || continue
    check_box "$id" "$host" "$port" "$gpu" "$status" || true
  done < <(ids_from_vast)
}

main "$@"
