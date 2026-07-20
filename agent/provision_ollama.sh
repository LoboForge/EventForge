#!/usr/bin/env bash
# Dedicated Ollama inference box — Dolphin 3.0 + forge-queue SQS agent (no Comfy).
#
# Vast extra_env reboot note: inline-export AWS_* / FORGE_QUEUE_* in onstart;
# agent loop persists creds in /workspace/.loboforge-env after first start.
set -euo pipefail

for _lf_ops_ssh in "$(dirname "${BASH_SOURCE[0]}")/ensure_ops_ssh.sh" "/workspace/ensure_ops_ssh.sh"; do
  [[ -f "$_lf_ops_ssh" ]] && . "$_lf_ops_ssh" && break
done
unset _lf_ops_ssh

_lo_bootstrap="/tmp/loboforge-worker-bootstrap-env.sh"
if curl -fsSL -A 'LoboForge-Worker/1.1' "${LOBO_BASE_URL:-https://www.loboforge.com}/agent/worker-bootstrap-env.sh" -o "$_lo_bootstrap" 2>/dev/null; then
  # shellcheck source=/dev/null
  . "$_lo_bootstrap"
fi
unset _lo_bootstrap
type lobo_forge_queue_env_defaults &>/dev/null && lobo_forge_queue_env_defaults || true

mkdir -p /workspace
cd /workspace

MODE="${MODE:-ollama}"
LOBO_SECRET="${LOBO_SECRET:-change-me-in-admin}"
LOBO_SERVER="${LOBO_SERVER:-wss://www.loboforge.com}"
LOBO_BASE_URL="${LOBO_BASE_URL:-https://www.loboforge.com}"
# LOBO_BASE_URL must be the LoboForge hub (active-loras / hub auth), NEVER EventForge.
case "$(printf '%s' "$LOBO_BASE_URL" | tr '[:upper:]' '[:lower:]')" in
  *eventforge.loboforge.com*) LOBO_BASE_URL="https://www.loboforge.com" ;;
esac
LOBO_BASE_URL="${LOBO_BASE_URL%/}"
LOBO_INSTANCE_ID="${LOBO_INSTANCE_ID:-${CONTAINER_ID:-unknown}}"
LOBO_LABEL="${LOBO_LABEL:-loboforge-ollama}"
LOBO_OLLAMA_MODEL="${LOBO_OLLAMA_MODEL:-dolphin3:8b}"
LOBO_OLLAMA_NUM_CTX="${LOBO_OLLAMA_NUM_CTX:-65536}"

export MODE LOBO_MODE="$MODE" LOBO_SECRET LOBO_SERVER LOBO_BASE_URL LOBO_INSTANCE_ID LOBO_LABEL
export LOBO_OLLAMA_MODEL LOBO_OLLAMA_NUM_CTX LOBOFORGE_AGENT_SECRET="$LOBO_SECRET"
export LOBO_WAN=0 LOBO_LTX23=0 LOBO_MUSIC=0
if [[ "${LOBO_GEN_QUEUE:-}" == "eventforge" ]]; then
  export FORGE_QUEUE_CAPABILITY=ollama-chat
  export EVENT_FORGE_CAPABILITY=ollama-chat
else
  export FORGE_QUEUE_CAPABILITY=dolphin
fi
export PYTHONPATH="/workspace${PYTHONPATH:+:$PYTHONPATH}"

PY="/venv/main/bin/python3"
[[ -x "$PY" ]] || PY="$(command -v python3)"

"$PY" -m pip install -q -U aiohttp boto3 2>/dev/null || true
type lobo_install_forge_queue_sdk &>/dev/null && lobo_install_forge_queue_sdk "$PY" || true

if type lobo_require_aws_creds &>/dev/null; then
  lobo_require_aws_creds || exit 1
elif [[ -z "${AWS_ACCESS_KEY_ID:-}" || -z "${AWS_SECRET_ACCESS_KEY:-}" ]]; then
  echo "ERROR: ForgeQueueWorker IAM required — AWS_ACCESS_KEY_ID/AWS_SECRET_ACCESS_KEY missing." >&2
  echo "  Admin: Fleet:ForgeQueue:AccessKey/SecretKey in appsettings.Secrets.json." >&2
  exit 1
fi

curl -fsSL -A "LoboForge-Worker/1.1" "$LOBO_BASE_URL/agent/loboforge_worker.tar.gz" -o /tmp/loboforge_worker.tar.gz
tar -xzf /tmp/loboforge_worker.tar.gz -C /workspace
rm -f /tmp/loboforge_worker.tar.gz

exec "$PY" -m loboforge_worker provision-ollama-native \
  --secret "$LOBO_SECRET" \
  --server "$LOBO_SERVER" \
  --base-url "$LOBO_BASE_URL" \
  --label "$LOBO_LABEL" \
  --model "$LOBO_OLLAMA_MODEL" \
  --num-ctx "$LOBO_OLLAMA_NUM_CTX" \
  2>&1 | tee /workspace/provision.log
