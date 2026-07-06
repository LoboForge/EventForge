#!/usr/bin/env bash
# Lightweight agent restart — no model downloads. Served at /agent/vast-agent-only-onstart.sh
set -euo pipefail

LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
_lo_bootstrap="/tmp/loboforge-worker-bootstrap-env.sh"
if curl -fsSL -A 'LoboForge-Worker/1.1' "${LOBO_BASE_URL}/agent/worker-bootstrap-env.sh" -o "$_lo_bootstrap" 2>/dev/null; then
  # shellcheck source=/dev/null
  . "$_lo_bootstrap"
elif [[ -f /workspace/ensure_ops_ssh.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/ensure_ops_ssh.sh
else
  for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
    [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
  done
fi
unset _lf_ops_ssh _lo_bootstrap
type lobo_ensure_ops_ssh &>/dev/null && lobo_ensure_ops_ssh || true

if [[ -z "${LOBO_SECRET:-}" || "${LOBO_SECRET}" == "change-me-in-admin" ]]; then
  echo "ERROR: LOBO_SECRET must be Workers:Secret from Vast extra_env (got '${LOBO_SECRET:-<unset>}')" >&2
  exit 1
fi
export LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
if type lobo_fetch_gen_queue_mode &>/dev/null; then
  lobo_fetch_gen_queue_mode
else
  _gq_json="$(curl -sf --max-time 10 "$LOBO_BASE_URL/api/agent/gen-queue-mode?secret=$LOBO_SECRET" || echo '{}')"
  LOBO_GEN_QUEUE="$(printf '%s' "$_gq_json" | python3 -c "import json,sys; print(json.load(sys.stdin).get('mode','sqs'))" 2>/dev/null || echo sqs)"
  export LOBO_GEN_QUEUE="${LOBO_GEN_QUEUE:-sqs}"
  unset _gq_json
fi
type lobo_forge_queue_env_defaults &>/dev/null && lobo_forge_queue_env_defaults || true
export LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-${CONTAINER_ID:-unknown}}"
export LOBO_LABEL="${LOBO_LABEL:-loboforge-all}"
export LOBO_MODE="${LOBO_MODE:-${MODE:-all}}"
export MODE="${MODE:-$LOBO_MODE}"
export LOBO_WAN="${LOBO_WAN:-1}"
_norm_mode="$(printf '%s' "$LOBO_MODE" | tr '[:upper:]' '[:lower:]' | cut -d, -f1)"
if [[ "$_norm_mode" == "all" || "$_norm_mode" == "both" ]]; then
  export LOBO_LTX23="${LOBO_LTX23:-1}"
else
  export LOBO_LTX23="${LOBO_LTX23:-0}"
fi
mkdir -p /workspace
cd /workspace

PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"
LOG="/workspace/loboforge-agent.log"
COMFY_HTTP="${LOBO_COMFYUI_HTTP:-http://127.0.0.1:18188}"
COMFY_WS="${LOBO_COMFYUI_WS:-ws://127.0.0.1:18188}"
SESSION="${LOBO_TMUX_SESSION:-loboforge-agent}"
ENV_FILE="/workspace/.loboforge-env"

curl -fsSL "$LOBO_BASE_URL/agent/loboforge_agent.py" -o /workspace/loboforge_agent.py
curl -fsSL "$LOBO_BASE_URL/agent/loboforge_agent_sqs.py" -o /workspace/loboforge_agent_sqs.py
curl -fsSL "$LOBO_BASE_URL/agent/loboforge_agent_sqs.py" -o /workspace/loboforge_agent_sqs.py 2>/dev/null || true
curl -fsSL "$LOBO_BASE_URL/agent/loboforge_agent_common.py" -o /workspace/loboforge_agent_common.py
curl -fsSL "$LOBO_BASE_URL/agent/wd14_tagger.py" -o /workspace/wd14_tagger.py 2>/dev/null || true
"$PY" -m pip install -q -U aiohttp websockets boto3 2>/dev/null || true
type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || \
  "$PY" -m pip install -q -U -e /workspace/forge-queue/sdk 2>/dev/null || true

if [[ ! -f /workspace/.loboforge-hostname ]]; then
  suf="${LOBO_INSTANCE_ID: -8}"
  prefix="$(printf '%s' "$LOBO_LABEL" | tr ' ' '-' | tr -cd '[:alnum:]-_' | tr '[:upper:]' '[:lower:]' | cut -c1-48)"
  printf '%s-%s' "$prefix" "$suf" > /workspace/.loboforge-hostname
fi
HN="$(cat /workspace/.loboforge-hostname)"

type lobo_resolve_forge_queue_capabilities &>/dev/null && lobo_resolve_forge_queue_capabilities "${MODE:-$LOBO_MODE}"
if type lobo_write_persisted_env &>/dev/null; then
  export LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
  lobo_write_persisted_env "$ENV_FILE"
else
{
  echo "export LOBO_MODE=\"$LOBO_MODE\""
  echo "export MODE=\"$MODE\""
  echo "export LOBO_WAN=\"$LOBO_WAN\""
  echo "export LOBO_LTX23=\"$LOBO_LTX23\""
  echo "export LOBO_GEN_QUEUE=\"$LOBO_GEN_QUEUE\""
  echo "export LOBO_BASE_URL=\"${LOBO_BASE_URL:-https://www.loboforge.com}\""
  echo "export FORGE_QUEUE_REGION=\"${FORGE_QUEUE_REGION:-us-east-2}\""
  echo "export FORGE_QUEUE_BUCKET=\"${FORGE_QUEUE_BUCKET:-}\""
  echo "export FORGE_QUEUE_PREFIX=\"${FORGE_QUEUE_PREFIX:-fq}\""
  [[ -n "${FORGE_QUEUE_CAPABILITY:-}" ]] && echo "export FORGE_QUEUE_CAPABILITY=\"$FORGE_QUEUE_CAPABILITY\""
  [[ -n "${HF_TOKEN:-}" ]] && echo "export HF_TOKEN=\"$HF_TOKEN\""
  [[ -n "${AWS_ACCESS_KEY_ID:-}" ]] && echo "export AWS_ACCESS_KEY_ID=\"$AWS_ACCESS_KEY_ID\""
  [[ -n "${AWS_SECRET_ACCESS_KEY:-}" ]] && echo "export AWS_SECRET_ACCESS_KEY=\"$AWS_SECRET_ACCESS_KEY\""
  [[ -n "${AWS_ACCESS_KEY_ID:-}" ]] && echo "export FORGE_QUEUE_ACCESS_KEY=\"$AWS_ACCESS_KEY_ID\""
  [[ -n "${AWS_SECRET_ACCESS_KEY:-}" ]] && echo "export FORGE_QUEUE_SECRET_KEY=\"$AWS_SECRET_ACCESS_KEY\""
} > "$ENV_FILE"
fi

COMFY_DIR="/opt/workspace-internal/ComfyUI"
[[ -d "$COMFY_DIR" ]] || COMFY_DIR="/workspace/comfyui"
if [[ -d "$COMFY_DIR/custom_nodes" ]]; then
  rgthree_dir="$COMFY_DIR/custom_nodes/rgthree-comfy"
  if [[ ! -d "$rgthree_dir/.git" ]]; then
    git clone --depth 1 https://github.com/rgthree/rgthree-comfy.git "$rgthree_dir" \
      || echo "WARN: rgthree clone failed"
  else
    git -C "$rgthree_dir" pull --ff-only 2>/dev/null || true
  fi
  if [[ -s "$rgthree_dir/requirements.txt" ]]; then
    "$PY" -m pip install -q -r "$rgthree_dir/requirements.txt" 2>/dev/null || true
  fi
fi

comfy_up() { curl -sf "$COMFY_HTTP/" >/dev/null 2>&1; }

start_comfy_if_down() {
  comfy_up && return 0
  tmux kill-session -t comfyui 2>/dev/null || true
  tmux new-session -d -s comfyui \
    "cd '$COMFY_DIR' && . /venv/main/bin/activate && LD_PRELOAD=libtcmalloc_minimal.so.4 python main.py --disable-auto-launch --port 18188 --listen 127.0.0.1 --enable-cors-header 2>&1 | tee /tmp/comfyui.log"
  for _ in $(seq 1 60); do comfy_up && return 0; sleep 2; done
  return 1
}

reload_comfy_plugins() {
  supervisorctl stop comfyui 2>/dev/null || true
  tmux kill-session -t comfyui 2>/dev/null || true
  pkill -f "python.*main.py" 2>/dev/null || true
  sleep 2
  tmux new-session -d -s comfyui \
    "cd '$COMFY_DIR' && . /venv/main/bin/activate && LD_PRELOAD=libtcmalloc_minimal.so.4 python main.py --disable-auto-launch --port 18188 --listen 127.0.0.1 --enable-cors-header 2>&1 | tee /tmp/comfyui.log"
  for _ in $(seq 1 90); do comfy_up && return 0; sleep 3; done
  return 1
}

power_lora_ok() {
  curl -sf "$COMFY_HTTP/object_info" 2>/dev/null \
    | "$PY" -c "import sys,json; d=json.load(sys.stdin); sys.exit(0 if 'Power Lora Loader (rgthree)' in d else 1)" \
    2>/dev/null
}

ltx_nodes_ok() {
  curl -sf "$COMFY_HTTP/object_info" 2>/dev/null \
    | "$PY" -c "import sys,json; d=json.load(sys.stdin); need=['ComfyMathExpression','LTXAVTextEncoderLoader','EmptyLTXVLatentVideo']; sys.exit(0 if all(n in d for n in need) else 1)" \
    2>/dev/null
}

ensure_ltx_comfy_stack() {
  [[ "${LOBO_LTX23:-0}" == "1" ]] || return 0
  ltx_nodes_ok && return 0
  ltx_dir="$COMFY_DIR/custom_nodes/ComfyUI-LTXVideo"
  if [[ -d "$ltx_dir/.git" ]]; then
    git -C "$ltx_dir" pull --ff-only 2>/dev/null || true
  else
    git clone --depth 1 https://github.com/Lightricks/ComfyUI-LTXVideo.git "$ltx_dir" \
      || echo "WARN: LTXVideo clone failed"
  fi
  "$PY" -m pip install -q simpleeval 2>/dev/null || true
  [[ -s "$ltx_dir/requirements.txt" ]] && "$PY" -m pip install -q -r "$ltx_dir/requirements.txt" 2>/dev/null || true
  reload_comfy_plugins || start_comfy_if_down || true
  ltx_nodes_ok && return 0
  if [[ -d "$COMFY_DIR/.git" ]]; then
    models_link=""
    [[ -L "$COMFY_DIR/models" ]] && models_link="$(readlink "$COMFY_DIR/models")" && rm -f "$COMFY_DIR/models"
    git -C "$COMFY_DIR" fetch origin master 2>/dev/null || git -C "$COMFY_DIR" fetch origin main 2>/dev/null || true
    git -C "$COMFY_DIR" pull --ff-only origin master 2>/dev/null \
      || git -C "$COMFY_DIR" pull --ff-only origin main 2>/dev/null || true
    [[ -n "$models_link" ]] && ln -sf "$models_link" "$COMFY_DIR/models"
    "$PY" -m pip install -q comfy-aimdo==0.4.8 comfy-kitchen==0.2.10 simpleeval 2>/dev/null || true
    reload_comfy_plugins || true
  fi
  ltx_nodes_ok && echo "LTX Comfy nodes OK" || echo "WARN: LTX nodes still missing"
}

start_comfy_if_down || true
if ! power_lora_ok; then
  reload_comfy_plugins || start_comfy_if_down || true
fi
ensure_ltx_comfy_stack

tmux kill-session -t "$SESSION" 2>/dev/null || true
sleep 1
pkill -f 'loboforge_worker run' 2>/dev/null || true
pkill -f 'loboforge_agent' 2>/dev/null || true

_aws_ok=0
if type lobo_aws_creds_present &>/dev/null; then
  lobo_aws_creds_present && _aws_ok=1
elif [[ -n "${AWS_ACCESS_KEY_ID:-}" && -n "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
  _aws_ok=1
fi
if [[ "$_aws_ok" != "1" ]]; then
  echo "ERROR: AWS IAM creds missing — set AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY (ForgeQueueWorker policy)" | tee -a "$LOG"
  exit 1
fi

AGENT_SCRIPT="/workspace/loboforge_agent_sqs.py"
[[ -f "$AGENT_SCRIPT" ]] || AGENT_SCRIPT="/workspace/loboforge_agent.py"

LOOP="set -a; [[ -f $ENV_FILE ]] && . $ENV_FILE; set +a; "
LOOP+="export PYTHONPATH=/workspace\${PYTHONPATH:+:\$PYTHONPATH}; "
LOOP+="while true; do set -a; [[ -f $ENV_FILE ]] && . $ENV_FILE; set +a; "
LOOP+="echo \"[\$(date -Is)] SQS agent start\" | tee -a $LOG; "
LOOP+="$PY $AGENT_SCRIPT --secret $LOBO_SECRET --hostname $HN --comfyui-http $COMFY_HTTP --comfyui-ws $COMFY_WS "
LOOP+="2>&1 | tee -a $LOG; sleep 5; done"
tmux new-session -d -s "$SESSION" "$LOOP"
echo "agent restarted hostname=$HN session=$SESSION transport=sqs gen_queue=${LOBO_GEN_QUEUE:-sqs}"
