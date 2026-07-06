#!/usr/bin/env bash
# Remote heal for stale Vast GPU boxes (invoked via Vast execute or curl|bash).
# Native LTX: connect-only relaunch. Comfy boxes: restart loboforge-agent tmux (SQS queue).
set -euo pipefail

LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
_lo_bootstrap="/tmp/loboforge-worker-bootstrap-env.sh"
if curl -fsSL -A 'LoboForge-Worker/1.1' "${LOBO_BASE_URL}/agent/worker-bootstrap-env.sh" -o "$_lo_bootstrap" 2>/dev/null; then
  # shellcheck source=/dev/null
  . "$_lo_bootstrap"
elif [[ -f /workspace/ensure_ops_ssh.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/ensure_ops_ssh.sh
fi
unset _lo_bootstrap
type lobo_ensure_ops_ssh &>/dev/null && lobo_ensure_ops_ssh || true

mkdir -p /workspace
cd /workspace

PY="${PY:-/venv/main/bin/python3}"
[[ -x "$PY" ]] || PY="$(command -v python3)"
BASE="${LOBO_BASE_URL%/}"
UA="LoboForge-Worker/1.1"
if [[ -z "${LOBO_SECRET:-}" || "${LOBO_SECRET}" == "change-me-in-admin" ]]; then
  echo "ERROR: LOBO_SECRET must be Workers:Secret from Vast extra_env" >&2
  exit 1
fi
SECRET="$LOBO_SECRET"
SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
type lobo_forge_queue_env_defaults &>/dev/null && lobo_forge_queue_env_defaults || true
[[ -n "${CONTAINER_ID:-}" ]] && export LOBO_INSTANCE_ID="${CONTAINER_ID}"
[[ -f /workspace/.loboforge-env ]] && set -a && . /workspace/.loboforge-env && set +a
if type lobo_write_persisted_env &>/dev/null; then
  export LOBO_BASE_URL="${LOBO_BASE_URL:-$BASE}"
  lobo_write_persisted_env /workspace/.loboforge-env || true
fi
if type lobo_fetch_gen_queue_mode &>/dev/null; then
  lobo_fetch_gen_queue_mode
else
  _gq_json="$(curl -sf --max-time 10 "${BASE}/api/agent/gen-queue-mode?secret=${SECRET}" || echo '{}')"
  export LOBO_GEN_QUEUE="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('mode','sqs'))" 2>/dev/null || echo sqs)"
  unset _gq_json
fi

lf_fetch() {
  curl -fsSL -A "$UA" "$1" -o "$2" \
    || "$PY" -c "import urllib.request; open('$2','wb').write(urllib.request.urlopen(urllib.request.Request('$1', headers={'User-Agent':'$UA'}), timeout=120).read())"
}

echo "[$(date -Is)] vast-heal-agent start label=${LOBO_LABEL:-} executor=${LOBO_EXECUTOR:-} gen_queue=${LOBO_GEN_QUEUE:-?}" >> /workspace/loboforge-agent.log

if [[ "${LOBO_EXECUTOR:-}" == "native" ]] || [[ "${LOBO_LABEL:-}" == *ltx* ]]; then
  export LOBO_EXECUTOR=native LOBO_SKIP_COMFY=1 LOBO_WAN=0 LOBO_LTX23=1 LOBO_MUSIC=0
  export MODE=ltx-native LOBO_MODE=ltx-native
  export LTX_MODEL_ROOT="${LTX_MODEL_ROOT:-/workspace/ltx-models}"
  export LTX_REPO="${LTX_REPO:-/workspace/LTX-2}"
  lf_fetch "$BASE/agent/loboforge_agent.py" /workspace/loboforge_agent.py
  if [[ ! -d /workspace/loboforge_worker ]]; then
    lf_fetch "$BASE/agent/loboforge_worker.tar.gz" /tmp/loboforge_worker.tar.gz
    tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
    rm -f /tmp/loboforge_worker.tar.gz
  fi
  export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"
  "$PY" -m loboforge_worker provision-ltx-native --connect-only \
    --secret "$SECRET" \
    --server "$SERVER" \
    --base-url "$BASE" \
    --instance-id "${LOBO_INSTANCE_ID:-unknown}" \
    --label "${LOBO_LABEL:-loboforge-ltx}" \
    2>&1 | tee -a /workspace/loboforge-agent.log
  lf_fetch "$BASE/agent/ltx-agent-watchdog.sh" /workspace/ltx-agent-watchdog.sh
  chmod +x /workspace/ltx-agent-watchdog.sh
  (crontab -l 2>/dev/null | grep -v 'ltx-agent-watchdog.sh' || true
   echo "*/5 * * * * bash /workspace/ltx-agent-watchdog.sh >> /workspace/ltx-watchdog.log 2>&1") | crontab -
  exit 0
fi

lf_fetch "$BASE/agent/loboforge_agent.py" /workspace/loboforge_agent.py
lf_fetch "$BASE/agent/loboforge_agent_sqs.py" /workspace/loboforge_agent_sqs.py
lf_fetch "$BASE/agent/loboforge_agent_sqs.py" /workspace/loboforge_agent_sqs.py 2>/dev/null || true
lf_fetch "$BASE/agent/loboforge_agent_common.py" /workspace/loboforge_agent_common.py
"$PY" -m pip install -q -U aiohttp websockets boto3 2>/dev/null || true
type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || true
tmux kill-session -t loboforge-agent 2>/dev/null || true
pkill -f 'loboforge_worker run' 2>/dev/null || true
pkill -f 'loboforge_agent' 2>/dev/null || true
HN="$(cat /workspace/.loboforge-hostname 2>/dev/null || echo "${LOBO_HOSTNAME:-loboforge-unknown}")"

_aws_ok=0
if type lobo_aws_creds_present &>/dev/null; then
  lobo_aws_creds_present && _aws_ok=1
elif [[ -n "${AWS_ACCESS_KEY_ID:-}" && -n "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
  _aws_ok=1
fi
if [[ "$_aws_ok" != "1" ]]; then
  echo "[$(date -Is)] vast-heal-agent: AWS IAM creds missing" >> /workspace/loboforge-agent.log
  exit 1
fi

LOOP="set -a; [[ -f /workspace/.loboforge-env ]] && . /workspace/.loboforge-env; set +a; "
LOOP+="export PYTHONPATH=/workspace\${PYTHONPATH:+:\$PYTHONPATH}; "
LOOP+="while true; do set -a; [[ -f /workspace/.loboforge-env ]] && . /workspace/.loboforge-env; set +a; "
LOOP+="echo \"[\$(date -Is)] heal SQS restart gen_queue=${LOBO_GEN_QUEUE:-sqs}\" | tee -a /workspace/loboforge-agent.log; "
LOOP+="$PY /workspace/loboforge_agent_sqs.py --secret $SECRET --hostname $HN "
LOOP+="--comfyui-http http://127.0.0.1:18188 --comfyui-ws ws://127.0.0.1:18188 "
LOOP+="2>&1 | tee -a /workspace/loboforge-agent.log; sleep 5; done"
tmux new-session -d -s loboforge-agent "$LOOP"
echo "[$(date -Is)] vast-heal-agent tmux started hostname=$HN transport=sqs gen_queue=${LOBO_GEN_QUEUE:-sqs}" >> /workspace/loboforge-agent.log
