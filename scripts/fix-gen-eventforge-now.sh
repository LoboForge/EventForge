#!/usr/bin/env bash
# Restart loboforge-image / loboforge-video Vast boxes on EventForge with prod secret + IAM creds.
# Does NOT touch joycaption fleet.
#
# Queue mode: auto-detected from the prod API (/api/agent/gen-queue-mode) — expects sqs.
# Override: LOBO_GEN_QUEUE=sqs bash scripts/fix-gen-eventforge-now.sh
#
# ForgeQueueWorker IAM (never commit keys — set one of):
#   export AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY
#   Prod API /api/agent/gen-queue-mode?secret=Workers:Secret (preferred — same as Vast rent extra_env)
#   Vast extra_env on each box (see admin Vast tab extra_env template)
#
# Coordinated cutover: WAIT_FOR_QUEUE=1 blocks until API reports mode=sqs (or EXPECT_MODE).
# Target specific boxes: INSTANCE_IDS="43243793 43243840" bash scripts/fix-gen-eventforge-now.sh
# Auto-discover all running loboforge-image/video boxes when INSTANCE_IDS is unset.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"
VAST_KEY="${VAST_API_KEY:?Set VAST_API_KEY or EventForge.VastAi.ApiKey in secrets.local.json}"
LOBO_SECRET="${LOBO_SECRET:-}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
EXPECT_MODE="${EXPECT_MODE:-eventforge}"
WAIT_FOR_QUEUE="${WAIT_FOR_QUEUE:-0}"
WAIT_MAX_SEC="${WAIT_MAX_SEC:-3600}"
STAGGER_SEC="${STAGGER_SEC:-90}"
GEN_WORKERS_EXPECT="${GEN_WORKERS_EXPECT:-4}"
FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-us-east-2}"
FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-}"
AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-}"

# ForgeQueueWorker IAM from prod API (same source as Vast rent extra_env) — never ~/.aws profile.

fetch_forge_queue_env_from_api() {
  GEN_QUEUE_JSON=$(curl -sf --max-time 15     "${LOBO_BASE_URL}/api/agent/gen-queue-mode?secret=${LOBO_SECRET}" || echo '{}')
  AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$(printf '%s' "$GEN_QUEUE_JSON"     | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueAccessKey',''))" 2>/dev/null || true)}"
  AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$(printf '%s' "$GEN_QUEUE_JSON"     | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueSecretKey',''))" 2>/dev/null || true)}"
}

fetch_gen_queue_mode() {
  GEN_QUEUE_JSON=$(curl -sf --max-time 10 \
    "${LOBO_BASE_URL}/api/agent/gen-queue-mode?secret=${LOBO_SECRET}" || echo '{}')
  LOBO_GEN_QUEUE=$(printf '%s' "$GEN_QUEUE_JSON" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('mode','sqs'))" 2>/dev/null || echo sqs)
  if [[ -z "$FORGE_QUEUE_BUCKET" ]]; then
    FORGE_QUEUE_BUCKET=$(printf '%s' "$GEN_QUEUE_JSON" \
      | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueBucket',''))" 2>/dev/null || true)
  fi
  if [[ -z "$FORGE_QUEUE_REGION" || "$FORGE_QUEUE_REGION" == "us-east-2" ]]; then
    local api_region
    api_region=$(printf '%s' "$GEN_QUEUE_JSON" \
      | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueueRegion',''))" 2>/dev/null || true)
    [[ -n "$api_region" ]] && FORGE_QUEUE_REGION="$api_region"
  fi
  if [[ -z "$FORGE_QUEUE_PREFIX" || "$FORGE_QUEUE_PREFIX" == "fq" ]]; then
    local api_prefix
    api_prefix=$(printf '%s' "$GEN_QUEUE_JSON" \
      | python3 -c "import json,sys; print(json.load(sys.stdin).get('forgeQueuePrefix',''))" 2>/dev/null || true)
    [[ -n "$api_prefix" ]] && FORGE_QUEUE_PREFIX="$api_prefix"
  fi
  EVENT_FORGE_URL="$(printf '%s' "$GEN_QUEUE_JSON" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeUrl',''))" 2>/dev/null || true)"
  EVENT_FORGE_WORKER_KEY="$(printf '%s' "$GEN_QUEUE_JSON" | python3 -c "import json,sys; print(json.load(sys.stdin).get('eventForgeWorkerKey',''))" 2>/dev/null || true)"
  export EVENT_FORGE_URL="${EVENT_FORGE_URL:-}"
  export EVENT_FORGE_WORKER_KEY="${EVENT_FORGE_WORKER_KEY:-}"
}

FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-forge-queue-994185520581-us-east-2}"
FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-us-east-2}"
FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"

if [[ -z "${LOBO_GEN_QUEUE:-}" ]]; then
  if [[ "$WAIT_FOR_QUEUE" == "1" ]]; then
    echo "Waiting for API gen-queue-mode=${EXPECT_MODE} (max ${WAIT_MAX_SEC}s)..."
    deadline=$((SECONDS + WAIT_MAX_SEC))
    while [[ $SECONDS -lt $deadline ]]; do
      fetch_gen_queue_mode
      echo "  poll mode=$LOBO_GEN_QUEUE"
      [[ "$LOBO_GEN_QUEUE" == "$EXPECT_MODE" ]] && break
      sleep 15
    done
    if [[ "$LOBO_GEN_QUEUE" != "$EXPECT_MODE" ]]; then
      echo "ERROR: API still mode=$LOBO_GEN_QUEUE after ${WAIT_MAX_SEC}s (expected $EXPECT_MODE)" >&2
      exit 1
    fi
  else
    fetch_gen_queue_mode
  fi
  echo "Detected API gen queue mode: $LOBO_GEN_QUEUE"
fi
LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-eventforge}"

fetch_forge_queue_env_from_api
fetch_gen_queue_mode
if [[ -z "${EVENT_FORGE_URL:-}" || -z "${EVENT_FORGE_WORKER_KEY:-}" ]]; then
  echo "ERROR: EventForge URL/worker key required — set EventForge:BaseUrl/WorkerKey in prod secrets." >&2
  exit 1
fi

REPO_AGENT_SHA=$(sha256sum "$ROOT/loboforge_agent_eventforge.py" | awk '{print $1}')
export INSTANCE_FILTER="${INSTANCE_IDS:-}"
CUTOVER_TS=$(date -Is)

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
    if not want and 'image' not in lab and 'video' not in lab and 'ltx' not in lab and 'wan-native' not in lab:
        continue
    if 'joycaption' in lab or 'caption' in lab:
        continue
    print(i['id'], i.get('ssh_host'), i.get('ssh_port'), i.get('label'))
")

if [[ ${#TARGETS[@]} -eq 0 ]]; then
  echo "No matching running loboforge-image/video/ltx instances (filter=${INSTANCE_FILTER:-auto})"
  exit 1
fi

echo "Cutover start: $CUTOVER_TS"
echo "Repo agent sha256=$REPO_AGENT_SHA gen_queue=$LOBO_GEN_QUEUE bucket=$FORGE_QUEUE_BUCKET filter=${INSTANCE_FILTER:-auto} targets=${#TARGETS[@]}"

box_diag() {
  local host="$1" port="$2"
  ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=30 "root@${host}" bash -s <<'REMOTE'
echo "--- ps loboforge ---"
ps aux | grep -E '[l]oboforge_agent|[l]oboforge_worker' || echo "(none)"
n=$(pgrep -fc '^/venv/main/bin/python3 /workspace/loboforge_agent_eventforge.py' 2>/dev/null | tr -d '\n' || echo 0)
sha=$(sha256sum /workspace/loboforge_agent_eventforge.py 2>/dev/null | awk '{print $1}' || echo missing)
aws_ok=$([[ -n "${AWS_ACCESS_KEY_ID:-}" && -n "${AWS_SECRET_ACCESS_KEY:-}" ]] && echo yes || echo no)
mode=$(grep -oE 'gen_queue=[a-z]+' /workspace/loboforge-agent.log 2>/dev/null | tail -1 | cut -d= -f2 || echo unknown)
conn=$(grep -c 'GPU EventForge agent starting' /workspace/loboforge-agent.log 2>/dev/null || echo 0)
echo "procs=$n sha=$sha aws_creds=$aws_ok last_mode=$mode ef_start_lines=$conn"
REMOTE
}

fix_one() {
  local id="$1" host="$2" port="$3" label="$4"
  local suffix="${id: -8}"
  local hn
  if [[ "$label" == loboforge-image ]]; then hn="loboforge-image-${suffix}"
  elif [[ "$label" == loboforge-video ]]; then hn="loboforge-video-${suffix}"
  elif [[ "$label" == loboforge-ltx ]]; then hn="loboforge-ltx-${suffix}"
  elif [[ "$label" == loboforge-wan-native ]]; then hn="loboforge-wan-native-${suffix}"
  else hn="$(echo "$label" | tr ' ' '-')-${suffix}"
  fi

  echo ""
  echo "▶ BEFORE $id ($label) $host:$port hostname=$hn"
  box_diag "$host" "$port" || echo "  (diag failed)"
  REPO_AGENT_SHA=$(sha256sum "$ROOT/agent/loboforge_agent_eventforge.py" | awk '{print $1}')

  scp -q -P "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 \
    "$ROOT/agent/loboforge_agent_eventforge.py" "$ROOT/agent/loboforge_agent.py" "$ROOT/agent/loboforge_agent_common.py" \
    "$ROOT/agent/loboforge_agent_sqs.py" \
    "root@${host}:/workspace/"
  ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 "root@${host}" \
    "mkdir -p /workspace/loboforge_worker/provision" 2>/dev/null || true
  if [[ -f "$ROOT/vendor/loboforge_worker/integration.py" ]]; then
    scp -q -P "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 \
      "$ROOT/vendor/loboforge_worker/integration.py" \
      "root@${host}:/workspace/loboforge_worker/integration.py"
  fi
  if [[ -f "$ROOT/vendor/loboforge_worker/provision/bootstrap_box.py" ]]; then
    scp -q -P "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 \
      "$ROOT/vendor/loboforge_worker/provision/bootstrap_box.py" \
      "root@${host}:/workspace/loboforge_worker/provision/bootstrap_box.py"
  fi
  if [[ -f "$ROOT/agent/worker-bootstrap-env.sh" ]]; then
    scp -q -P "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 \
      "$ROOT/agent/worker-bootstrap-env.sh" "root@${host}:/workspace/worker-bootstrap-env.sh"
  fi

  if ! ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=60 "root@${host}" \
    "LOBO_SECRET=$(printf '%q' "$LOBO_SECRET") HN=$(printf '%q' "$hn") LOBO_GEN_QUEUE=$(printf '%q' "$LOBO_GEN_QUEUE") LOBO_BASE_URL=$(printf '%q' "$LOBO_BASE_URL") LOBO_SERVER=$(printf '%q' "$LOBO_SERVER") REPO_AGENT_SHA=$(printf '%q' "$REPO_AGENT_SHA") AWS_ACCESS_KEY_ID=$(printf '%q' "$AWS_ACCESS_KEY_ID") AWS_SECRET_ACCESS_KEY=$(printf '%q' "$AWS_SECRET_ACCESS_KEY") FORGE_QUEUE_REGION=$(printf '%q' "$FORGE_QUEUE_REGION") FORGE_QUEUE_BUCKET=$(printf '%q' "$FORGE_QUEUE_BUCKET") FORGE_QUEUE_PREFIX=$(printf '%q' "$FORGE_QUEUE_PREFIX") EVENT_FORGE_URL=$(printf '%q' "$EVENT_FORGE_URL") EVENT_FORGE_WORKER_KEY=$(printf '%q' "$EVENT_FORGE_WORKER_KEY") bash -s" <<'REMOTE'
set -euo pipefail
PATCH_GEN_QUEUE="${LOBO_GEN_QUEUE:-eventforge}"
PATCH_EF_URL="${EVENT_FORGE_URL:-}"
PATCH_EF_KEY="${EVENT_FORGE_WORKER_KEY:-}"
PATCH_LOBO_SECRET="${LOBO_SECRET:-}"
[[ -f /workspace/.loboforge-env ]] && source /workspace/.loboforge-env || true
LOBO_GEN_QUEUE="$PATCH_GEN_QUEUE"
EVENT_FORGE_URL="$PATCH_EF_URL"
EVENT_FORGE_WORKER_KEY="$PATCH_EF_KEY"
LOBO_SECRET="$PATCH_LOBO_SECRET"
if [[ -n "${FORGE_QUEUE_ACCESS_KEY:-}" && -n "${FORGE_QUEUE_SECRET_KEY:-}" ]]; then
  export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$FORGE_QUEUE_ACCESS_KEY}"
  export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$FORGE_QUEUE_SECRET_KEY}"
fi

remote_sha=$(sha256sum /workspace/loboforge_agent_eventforge.py | awk '{print $1}')
if [[ "$remote_sha" != "$REPO_AGENT_SHA" ]]; then
  echo "ERROR: agent sha mismatch remote=$remote_sha expected=$REPO_AGENT_SHA" >&2
  exit 1
fi

if [[ "$PATCH_GEN_QUEUE" != "eventforge" ]]; then
  if [[ -z "${AWS_ACCESS_KEY_ID:-}" || -z "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
    echo "ERROR: AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY must be set in Vast extra_env" >&2
    exit 1
  fi
fi

ENV_FILE=/workspace/.loboforge-env
touch "$ENV_FILE"
python3 - "$ENV_FILE" "$PATCH_GEN_QUEUE" "$LOBO_BASE_URL" "$FORGE_QUEUE_REGION" "$FORGE_QUEUE_BUCKET" "$FORGE_QUEUE_PREFIX" "$AWS_ACCESS_KEY_ID" "$AWS_SECRET_ACCESS_KEY" "${EVENT_FORGE_URL:-}" "${EVENT_FORGE_WORKER_KEY:-}" "${LOBO_SECRET:-}" <<'PY'
import pathlib, sys
path, mode, base, region, bucket, prefix, ak, sk, ef_url, ef_key, lobo_secret = sys.argv[1:12]
lines = pathlib.Path(path).read_text(encoding="utf-8").splitlines() if pathlib.Path(path).is_file() else []
keys = {
    "LOBO_SECRET": lobo_secret,
    "LOBO_GEN_QUEUE": mode,
    "LOBO_BASE_URL": base,
    "FORGE_QUEUE_REGION": region,
    "FORGE_QUEUE_BUCKET": bucket,
    "FORGE_QUEUE_PREFIX": prefix,
    "AWS_ACCESS_KEY_ID": ak,
    "AWS_SECRET_ACCESS_KEY": sk,
    "FORGE_QUEUE_ACCESS_KEY": ak,
    "FORGE_QUEUE_SECRET_KEY": sk,
    "EVENT_FORGE_URL": ef_url,
    "EVENT_FORGE_WORKER_KEY": ef_key,
}
out, seen = [], set()
for line in lines:
    stripped = line.strip()
    if not stripped or stripped.startswith("#"):
        out.append(line)
        continue
    body = stripped[7:].strip() if stripped.startswith("export ") else stripped
    key = body.split("=", 1)[0].strip()
    if key in keys:
        out.append(f'export {key}="{keys[key]}"')
        seen.add(key)
    else:
        out.append(line)
for key, val in keys.items():
    if key not in seen:
        out.append(f'export {key}="{val}"')
pathlib.Path(path).write_text("\n".join(out).rstrip() + "\n", encoding="utf-8")
PY
/venv/main/bin/pip install -q -U aiohttp websockets boto3 2>/dev/null || pip install -q -U aiohttp websockets boto3
sdk_dir="/workspace/forge-queue/sdk"
if [[ ! -f "$sdk_dir/pyproject.toml" ]]; then
  if curl -fsSL -A 'LoboForge-Worker/1.1' "${LOBO_BASE_URL}/agent/forge-queue-sdk.tar.gz" -o /tmp/forge-queue-sdk.tar.gz 2>/dev/null; then
    mkdir -p /workspace
    tar -xzf /tmp/forge-queue-sdk.tar.gz -C /workspace
    rm -f /tmp/forge-queue-sdk.tar.gz
    sdk_dir="/workspace/forge-queue/sdk"
  fi
fi
if [[ -f "$sdk_dir/pyproject.toml" ]]; then
  /venv/main/bin/pip install -q -U -e "$sdk_dir" 2>/dev/null \
    || pip install -q -U -e "$sdk_dir" 2>/dev/null || true
else
  echo "WARN: forge-queue SDK missing (not required for EventForge)"
fi

tmux kill-session -t loboforge-agent 2>/dev/null || true
pkill -9 -f 'loboforge_worker run' 2>/dev/null || true
pkill -9 -f 'loboforge_agent' 2>/dev/null || true
sleep 5

LOG=/workspace/loboforge-agent.log
: > "$LOG"
tmux new-session -d -s loboforge-agent "bash -lc '
source /workspace/.loboforge-env 2>/dev/null || true
export LOBO_SECRET=\"${LOBO_SECRET}\"
export HN=\"${HN}\"
export PYTHONPATH=/workspace
export LOG=/workspace/loboforge-agent.log
while true; do
  source /workspace/.loboforge-env 2>/dev/null || true
  echo \"[\$(date -Is)] starting EventForge GPU agent (gen_queue=${LOBO_GEN_QUEUE})...\" | tee -a \"\$LOG\"
  /venv/main/bin/python3 /workspace/loboforge_agent_eventforge.py \
    --secret \"\$LOBO_SECRET\" --hostname \"\$HN\" \
    --comfyui-http http://127.0.0.1:18188 --comfyui-ws ws://127.0.0.1:18188 \
    2>&1 | tee -a \"\$LOG\"
  echo \"[\$(date -Is)] agent exited, restart in 5s\" | tee -a \"\$LOG\"
  sleep 5
done'"
sleep 10
tail -15 "$LOG" 2>/dev/null || true
procs=0
for _try in 1 2 3 4 5; do
  procs=$(pgrep -fc '^/venv/main/bin/python3 /workspace/loboforge_agent_eventforge.py' 2>/dev/null | tr -d '\n' || echo 0)
  [[ "${procs:-0}" -eq 1 ]] && break
  sleep 5
done
conn_line=$(grep 'GPU EventForge agent starting' "$LOG" 2>/dev/null | tail -1 || echo "(none)")
echo "agent_procs=$procs sha=$remote_sha gen_queue=${LOBO_GEN_QUEUE}"
echo "verify_conn=$conn_line"
if [[ "${procs:-0}" -ne 1 ]]; then
  echo "ERROR: expected exactly 1 loboforge_agent_eventforge.py process, got ${procs:-0}" >&2
  exit 1
fi
source "$ENV_FILE" 2>/dev/null || true
LOBO_SECRET="$PATCH_LOBO_SECRET"
EVENT_FORGE_URL="${EVENT_FORGE_URL:-http://eventforge.loboforge.local:8090}"
if curl -sf --max-time 10 -A 'LoboForge-Worker/1.1' "${EVENT_FORGE_URL}/health" >/dev/null; then
  echo "eventforge_health=ok url=${EVENT_FORGE_URL}"
else
  echo "WARN: EventForge /health unreachable from box (agent check-in is authoritative) url=${EVENT_FORGE_URL}" >&2
fi
REMOTE
  then
    echo "ERROR: fix failed on $id" >&2
    exit 1
  fi

  # Background LoRA sync so workers do not defer-loop on missing assets.
  LORA_MODE="all"
  if [[ "$label" == loboforge-video || "$label" == loboforge-wan-native ]]; then LORA_MODE="video"
  elif [[ "$label" == loboforge-image ]]; then LORA_MODE="image"
  fi
  echo "▶ LoRA sync $id (mode=$LORA_MODE, background)"
  ssh -p "$port" -o StrictHostKeyChecking=no -o ConnectTimeout=30 "root@${host}"     "LOBO_SECRET=$(printf '%q' "$LOBO_SECRET") LOBO_BASE_URL=$(printf '%q' "$LOBO_BASE_URL") LORA_MODE=$(printf '%q' "$LORA_MODE") bash -s" <<'LORASYNC' || echo "  (LoRA sync launch warning)"
set -euo pipefail
nohup bash -lc '
  source /workspace/.loboforge-env 2>/dev/null || true
  export PYTHONPATH=/workspace
  /venv/main/bin/python3 -m loboforge_worker sync-loras     --base-url "$LOBO_BASE_URL" --secret "$LOBO_SECRET" --mode "$LORA_MODE"     >> /workspace/lora-sync.log 2>&1
' >/dev/null 2>&1 &
echo lora_sync_started
LORASYNC

  echo "▶ AFTER $id"
  box_diag "$host" "$port" || true
}

idx=0
total=${#TARGETS[@]}
for line in "${TARGETS[@]}"; do
  read -r id host port label <<< "$line"
  fix_one "$id" "$host" "$port" "$label"
  idx=$((idx + 1))
  if [[ $idx -lt $total ]]; then
    echo "  waiting ${STAGGER_SEC}s before next box..."
    sleep "$STAGGER_SEC"
  fi
done

echo ""
echo "Polling EventForge fleet (expect ${GEN_WORKERS_EXPECT} workers: image+video+ltx+ollama)..."
for i in $(seq 1 36); do
  st=$(curl -sf -H "Authorization: Bearer ${EVENT_FORGE_WORKER_KEY}" \
    "${EVENT_FORGE_URL}/v1/fleet/workers" || echo '{}')
  gen_live=$(python3 -c "
import json,sys
d=json.load(sys.stdin)
ws=[w for w in d.get('workers',[]) if any(x in (w.get('hostname') or '').lower() for x in ('image','video','ltx','ollama'))]
print(len(ws), ','.join(sorted(w.get('hostname','') for w in ws)))
" <<< "$st")
  gen_count="${gen_live%% *}"
  gen_hosts="${gen_live#* }"
  echo "  poll $i eventforge_workers=${gen_count}/$GEN_WORKERS_EXPECT [$gen_hosts]"
  [[ "$gen_count" -ge "$GEN_WORKERS_EXPECT" ]] && break
  sleep 10
done

echo ""
echo "=== CUTOVER SUMMARY ==="
echo "cutover_ts=$CUTOVER_TS finished=$(date -Is) gen_queue=$LOBO_GEN_QUEUE repo_agent_sha=$REPO_AGENT_SHA"
