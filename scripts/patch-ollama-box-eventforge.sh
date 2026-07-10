#!/usr/bin/env bash
# Switch a running loboforge-ollama Vast box to EventForge transport.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"
die() { echo "✗ $*" >&2; exit 1; }
step() { echo "▶ $*"; }

discover_ssh() {
  python3 <<PY
import json, os, urllib.request
key = os.environ.get("VAST_API_KEY", "")
req = urllib.request.Request("https://console.vast.ai/api/v0/instances/", headers={"Authorization": f"Bearer {key}"})
for i in json.loads(urllib.request.urlopen(req, timeout=60).read()).get("instances", []):
    if (i.get("actual_status") or "").lower() != "running":
        continue
    if "ollama" in (i.get("label") or "").lower() or "dolphin" in (i.get("label") or "").lower():
        print(i.get("ssh_host") or "ssh1.vast.ai", i.get("ssh_port") or 22)
        break
PY
}

if [[ $# -ge 2 ]]; then SSH_HOST="$1"; SSH_PORT="$2"
else read -r SSH_HOST SSH_PORT < <(discover_ssh) || die "No running loboforge-ollama instance found"; fi

SSH="ssh -p ${SSH_PORT} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null root@${SSH_HOST}"
SCP="scp -P ${SSH_PORT} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null"
LOBO_SECRET="${LOBO_SECRET:?Set LOBO_SECRET or LoboForge.WorkersSecret in secrets.local.json}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
GEN_QUEUE_JSON=$(curl -sf --max-time 15 "${LOBO_BASE_URL}/api/agent/gen-queue-mode?secret=${LOBO_SECRET}" || echo '{}')
EVENT_FORGE_URL=$(printf '%s' "$GEN_QUEUE_JSON" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeUrl',''))" 2>/dev/null || true)
EVENT_FORGE_WORKER_KEY=$(printf '%s' "$GEN_QUEUE_JSON" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeWorkerKey',''))" 2>/dev/null || true)
[[ -n "$EVENT_FORGE_URL" && -n "$EVENT_FORGE_WORKER_KEY" ]] || die "EventForge URL/worker key missing on API"

step "Patching loboforge-ollama on ${SSH_HOST}:${SSH_PORT} → EventForge"
$SCP "${ROOT}/agent/loboforge_ollama_agent_eventforge.py" "root@${SSH_HOST}:/workspace/loboforge_ollama_agent_eventforge.py"
$SCP "${ROOT}/scripts/vast-ollama-eventforge-agent-loop.sh" "root@${SSH_HOST}:/workspace/vast-ollama-eventforge-agent-loop.sh"
$SCP "${ROOT}/agent/worker-bootstrap-env.sh" "root@${SSH_HOST}:/workspace/worker-bootstrap-env.sh"

$SSH "LOBO_SECRET=$(printf '%q' "$LOBO_SECRET") LOBO_BASE_URL=$(printf '%q' "$LOBO_BASE_URL") EVENT_FORGE_URL=$(printf '%q' "$EVENT_FORGE_URL") EVENT_FORGE_WORKER_KEY=$(printf '%q' "$EVENT_FORGE_WORKER_KEY") bash -s" <<'REMOTE'
set -euo pipefail
PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"
ENV_FILE=/workspace/.loboforge-env
touch "$ENV_FILE"
for kv in LOBO_SECRET LOBO_BASE_URL EVENT_FORGE_URL EVENT_FORGE_WORKER_KEY; do
  val="${!kv:-}"
  [[ -n "$val" ]] || continue
  grep -q "^export ${kv}=" "$ENV_FILE" 2>/dev/null && sed -i "s|^export ${kv}=.*|export ${kv}=\"${val}\"|" "$ENV_FILE" || echo "export ${kv}=\"${val}\"" >> "$ENV_FILE"
done
grep -q "^export EVENT_FORGE_CAPABILITY=" "$ENV_FILE" 2>/dev/null && sed -i 's|^export EVENT_FORGE_CAPABILITY=.*|export EVENT_FORGE_CAPABILITY="ollama-chat"|' "$ENV_FILE" || echo 'export EVENT_FORGE_CAPABILITY="ollama-chat"' >> "$ENV_FILE"
grep -q "^export FORGE_QUEUE_CAPABILITY=" "$ENV_FILE" 2>/dev/null && sed -i 's|^export FORGE_QUEUE_CAPABILITY=.*|export FORGE_QUEUE_CAPABILITY="ollama-chat"|' "$ENV_FILE" || echo 'export FORGE_QUEUE_CAPABILITY="ollama-chat"' >> "$ENV_FILE"
grep -q "^export LOBO_GEN_QUEUE=" "$ENV_FILE" 2>/dev/null && sed -i 's|^export LOBO_GEN_QUEUE=.*|export LOBO_GEN_QUEUE="eventforge"|' "$ENV_FILE" || echo 'export LOBO_GEN_QUEUE="eventforge"' >> "$ENV_FILE"
grep -q "^export LOBO_LABEL=" "$ENV_FILE" 2>/dev/null || echo 'export LOBO_LABEL="loboforge-ollama"' >> "$ENV_FILE"
grep -q "^export LOBO_INSTANCE_ID=" "$ENV_FILE" 2>/dev/null || echo 'export LOBO_INSTANCE_ID="42600549"' >> "$ENV_FILE"
"$PY" -m pip install -q -U aiohttp
chmod +x /workspace/vast-ollama-eventforge-agent-loop.sh
tmux kill-session -t loboforge-ollama-agent 2>/dev/null || true
pkill -f 'loboforge_ollama_agent' 2>/dev/null || true
sleep 2
tmux new-session -d -s loboforge-ollama-agent 'source /workspace/.loboforge-env 2>/dev/null; /workspace/vast-ollama-eventforge-agent-loop.sh'
echo "Ollama EventForge agent restarted"
REMOTE
step "Done"
