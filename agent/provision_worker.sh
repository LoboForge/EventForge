#!/usr/bin/env bash
# Thin bootstrap for new Vast.ai boxes — delegates to loboforge_worker bootstrap.
# Served at /agent/provision_worker.sh (replaces provision_gpu.sh for new rents).
#
# Vast extra_env reboot note: Vast may not pass extra_env to nohup/curl|bash children.
# Onstart scripts inline-export AWS_* / FORGE_QUEUE_* before invoking this script.
# After reboot, agent loops re-read /workspace/.loboforge-env written at first provision.
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

mkdir -p /workspace
cd /workspace

MODE="${MODE:-all}"
if [[ -z "${LOBO_SECRET:-}" || "${LOBO_SECRET}" == "change-me-in-admin" ]]; then
  echo "ERROR: LOBO_SECRET must be Workers:Secret from Vast extra_env" >&2
  exit 1
fi
LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
LOBO_SCRIPT_FALLBACK="${LOBO_SCRIPT_FALLBACK:-https://www.loboforge.com}"
LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-${CONTAINER_ID:-unknown}}"

# HF_TOKEN must come from Vast extra_env (never commit tokens to git).

while [[ $# -gt 0 ]]; do
    case "$1" in
        --mode) MODE="$2"; shift 2 ;;
        --help|-h)
            echo "Usage: $0 [--mode image|video|music|all|<comma-list>]"
            exit 0
            ;;
        *) shift ;;
    esac
done

# After --mode is resolved — default label must not be computed while MODE still defaults to all.
LOBO_LABEL="${LOBO_LABEL:-loboforge-${MODE}}"

HF_TOKEN="${HF_TOKEN:-}"
export MODE LOBO_SECRET LOBO_SERVER LOBO_BASE_URL LOBO_INSTANCE_ID LOBO_LABEL
export LOBO_MODE="$MODE"
export LOBO_WAN="${LOBO_WAN:-1}"
_norm_mode="$(printf '%s' "$MODE" | tr '[:upper:]' '[:lower:]' | cut -d, -f1)"
if [[ "$_norm_mode" == "all" || "$_norm_mode" == "both" || "$_norm_mode" == "ltx" || "$_norm_mode" == "ltx-native" ]]; then
  export LOBO_LTX23="${LOBO_LTX23:-1}"
else
  export LOBO_LTX23="${LOBO_LTX23:-0}"
fi
export HF_TOKEN HUGGINGFACE_HUB_TOKEN="$HF_TOKEN"
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/worker-bootstrap-env.sh
  lobo_resolve_forge_queue_capabilities "$MODE"
else
  _caps=""
  case "$_norm_mode" in
    image) _caps="flux-klein,flux-klein-edit,zimage,chroma" ;;
    video)
      [[ "${LOBO_WAN:-1}" != "0" ]] && _caps="wan"
      if [[ "${LOBO_LTX23:-0}" == "1" ]]; then
        [[ -n "$_caps" ]] && _caps="${_caps},ltx" || _caps="ltx"
      fi
      [[ -z "$_caps" ]] && _caps="wan"
      ;;
    music) _caps="ltx" ;;
    all|both)
      _caps="flux-klein,flux-klein-edit,zimage,chroma"
      [[ "${LOBO_WAN:-1}" != "0" ]] && _caps="${_caps},wan"
      [[ "${LOBO_LTX23:-0}" == "1" ]] && _caps="${_caps},ltx"
      ;;
    ltx-native|ltx) _caps="ltx" ;;
    *) _caps="flux-klein" ;;
  esac
  export FORGE_QUEUE_CAPABILITY="$_caps"
fi

# forge-queue SQS env (IAM creds from Vast extra_env — no IoT certs)
# Jobs: forge-queue SQS only. API: check-in + LoRA prefetch (request-work) only.
export FORGE_QUEUE_REGION="${FORGE_QUEUE_REGION:-${AWS_REGION:-us-east-2}}"
export FORGE_QUEUE_BUCKET="${FORGE_QUEUE_BUCKET:-}"
export FORGE_QUEUE_PREFIX="${FORGE_QUEUE_PREFIX:-fq}"
if [[ -n "${FORGE_QUEUE_ACCESS_KEY:-}" && -n "${FORGE_QUEUE_SECRET_KEY:-}" ]]; then
  export AWS_ACCESS_KEY_ID="${AWS_ACCESS_KEY_ID:-$FORGE_QUEUE_ACCESS_KEY}"
  export AWS_SECRET_ACCESS_KEY="${AWS_SECRET_ACCESS_KEY:-$FORGE_QUEUE_SECRET_KEY}"
fi
export AWS_DEFAULT_REGION="${AWS_DEFAULT_REGION:-$FORGE_QUEUE_REGION}"

_gen_queue="${LOBO_GEN_QUEUE:-}"
if [[ -z "$_gen_queue" ]] && type lobo_fetch_gen_queue_mode &>/dev/null; then
  lobo_fetch_gen_queue_mode || true
  _gen_queue="${LOBO_GEN_QUEUE:-}"
fi

if [[ "$_gen_queue" != "eventforge" ]]; then
  if [[ -z "${AWS_ACCESS_KEY_ID:-}" || -z "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
    echo "ERROR: ForgeQueueWorker IAM required — AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY missing." >&2
    echo "  Admin: Fleet:ForgeQueue:AccessKey/SecretKey in appsettings.Secrets.json (new rents inject via Vast extra_env)." >&2
    exit 1
  fi
fi
unset _gen_queue

PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"

# Agent imports need these before bootstrap touches loboforge_agent.py.
"$PY" -m pip install -q -U websockets aiohttp gdown huggingface_hub boto3 2>/dev/null || true

# Always refresh from prod — onstart only runs once; a failed first attempt must not
# leave a stale loboforge_worker tree (regression: import-before-pip in old bundle).
_lf_fetch() {
  local file="$1" dest="$2" optional="${3:-0}"
  local base
  local bases=("${EVENT_FORGE_URL:-https://eventforge.loboforge.com}")
  bases+=("$LOBO_BASE_URL" "$LOBO_SCRIPT_FALLBACK")
  for base in "${bases[@]}"; do
    if curl -fsSL -A 'LoboForge-Worker/1.1' "${base%/}/agent/$file" -o "$dest"; then
      return 0
    fi
  done
  [[ "$optional" == 1 ]] && return 0
  return 1
}
_lf_fetch loboforge_worker.tar.gz /tmp/loboforge_worker.tar.gz
tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
rm -f /tmp/loboforge_worker.tar.gz
_lf_fetch loboforge_agent.py /workspace/loboforge_agent.py
_lf_fetch loboforge_agent_common.py /workspace/loboforge_agent_common.py
_lf_fetch loboforge_agent_sqs.py /workspace/loboforge_agent_sqs.py
_lf_fetch loboforge_agent_eventforge.py /workspace/loboforge_agent_eventforge.py
_lf_fetch wd14_tagger.py /workspace/wd14_tagger.py 1 || true
_lf_fetch worker-bootstrap-env.sh /workspace/worker-bootstrap-env.sh
if [[ -f /workspace/worker-bootstrap-env.sh ]]; then
  # shellcheck source=/dev/null
  . /workspace/worker-bootstrap-env.sh
  lobo_fetch_gen_queue_mode || true
  lobo_install_forge_queue_sdk "$PY" || true
fi

if type lobo_write_persisted_env &>/dev/null; then
  lobo_write_persisted_env /workspace/.loboforge-env
fi

"$PY" -m loboforge_worker bootstrap --mode "$MODE" 2>&1 | tee /workspace/provision.log

# Background LoRA sync for this box's mode (image/video/all) from loboforge.com.
_lora_mode="all"
case "$_norm_mode" in
  video) _lora_mode="video" ;;
  image) _lora_mode="image" ;;
esac
nohup bash -lc "
  source /workspace/.loboforge-env 2>/dev/null || true
  export PYTHONPATH=/workspace
  $PY -m loboforge_worker sync-loras --base-url \"$LOBO_BASE_URL\" --secret \"$LOBO_SECRET\" --mode \"$_lora_mode\"
" >> /workspace/lora-sync.log 2>&1 &

# Model downloads: tmux session loboforge-provision (started by bootstrap)
#   tail -f /workspace/model-provision.log
# LoRA sync log: tail -f /workspace/lora-sync.log
