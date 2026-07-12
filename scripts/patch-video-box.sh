#!/usr/bin/env bash
# Patch and bootstrap a Vast video box for EventForge wan queue.
# Usage: patch-video-box.sh <instance_id> <ssh_host> <ssh_port>
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"

ID="${1:?instance id}"
HOST="${2:?ssh host}"
PORT="${3:?ssh port}"
EF_KEY="${EVENT_FORGE_WORKER_KEY:-wrath-ef-ff4ec2ee76871a822c5fc9e8}"
LOG="/tmp/patch-${ID}.log"
HOSTNAME="loboforge-video-${ID}"

patch_once() {
  echo "PATCHING $ID $(date -Is)" | tee -a "$LOG"
  scp -q -P "$PORT" -o StrictHostKeyChecking=no \
    "$ROOT/agent/provision_worker.sh" "$ROOT/agent/worker-bootstrap-env.sh" \
    "$ROOT/agent/loboforge_agent_eventforge.py" "$ROOT/agent/loboforge_agent_common.py" \
    "$ROOT/agent/loboforge_agent.py" "$ROOT/agent/loboforge_worker.tar.gz" \
    "root@${HOST}:/workspace/"
  ssh -p "$PORT" -o StrictHostKeyChecking=no "root@${HOST}" bash -s <<REMOTE
set -e
cd /workspace
export LOBO_SECRET='${LOBO_SECRET}' LOBO_SERVER='wss://www.loboforge.com' LOBO_BASE_URL='https://www.loboforge.com'
export LOBO_GEN_QUEUE='eventforge' EVENT_FORGE_URL='https://eventforge.loboforge.com' EVENT_FORGE_WORKER_KEY='${EF_KEY}'
export LOBO_WAN=1 LOBO_LTX23=0 MODE=video LOBO_MODE=video LOBO_INSTANCE_ID='${ID}' LOBO_LABEL='${HOSTNAME}' FORGE_QUEUE_CAPABILITY='wan'
chmod +x worker-bootstrap-env.sh provision_worker.sh
. worker-bootstrap-env.sh && lobo_write_persisted_env /workspace/.loboforge-env
tar -xzf loboforge_worker.tar.gz -C /workspace
pkill -f loboforge_agent_eventforge || true
/venv/main/bin/python3 -m loboforge_worker bootstrap --mode video 2>&1 | tee /workspace/provision.log
REMOTE
}

echo "watch $ID $HOST:$PORT $(date -Is)" >> "$LOG"
for _ in $(seq 1 120); do
  if ssh -p "$PORT" -o StrictHostKeyChecking=no -o ConnectTimeout=12 "root@${HOST}" "echo up" 2>/dev/null; then
    patch_once
    echo "done $ID $(date -Is)" >> "$LOG"
    exit 0
  fi
  echo "$(date -Is) waiting ssh" >> "$LOG"
  sleep 30
done
echo "timeout $ID $(date -Is)" >> "$LOG"
exit 1
