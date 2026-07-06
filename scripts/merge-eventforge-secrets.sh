#!/usr/bin/env bash
# Merge EventForge + Fleet.GenQueue.Mode into loboforge/app-secrets.
set -euo pipefail
REGION="${AWS_REGION:-us-east-2}"
SECRET_ID="${APP_SECRET_ID:-loboforge/app-secrets}"
die() { echo "ERROR: $*" >&2; exit 1; }

API_KEY="${EVENT_FORGE_API_KEY:-}"
WORKER_KEY="${EVENT_FORGE_WORKER_KEY:-}"
BASE_URL="${EVENT_FORGE_BASE_URL:-https://eventforge.loboforge.com}"
WORKER_URL="${EVENT_FORGE_WORKER_BASE_URL:-${EVENT_FORGE_WORKER_URL:-$BASE_URL}}"
WS_URL="${EVENT_FORGE_WS_URL:-wss://eventforge.loboforge.com/v1/ws}"

echo "▶ Reading ${SECRET_ID}..."
current=$(aws secretsmanager get-secret-value --secret-id "$SECRET_ID" --region "$REGION" --query SecretString --output text)

if [[ -z "$API_KEY" ]]; then
  API_KEY=$(printf '%s' "$current" | python3 -c "import json,sys; print(json.load(sys.stdin).get('EventForge',{}).get('ApiKey',''))" 2>/dev/null || true)
fi
if [[ -z "$WORKER_KEY" ]]; then
  WORKER_KEY=$(printf '%s' "$current" | python3 -c "import json,sys; print(json.load(sys.stdin).get('EventForge',{}).get('WorkerKey',''))" 2>/dev/null || true)
fi
[[ -n "$API_KEY" ]] || API_KEY="loboforge-ef-$(openssl rand -hex 12)"
[[ -n "$WORKER_KEY" ]] || WORKER_KEY="wrath-ef-$(openssl rand -hex 12)"
export API_KEY WORKER_KEY BASE_URL WORKER_URL WS_URL
merged=$(printf '%s' "$current" | python3 -c "
import json, os, sys
data = json.load(sys.stdin)
data.setdefault('Fleet', {}).setdefault('GenQueue', {})['Mode'] = 'eventforge'
ef = data.setdefault('EventForge', {})
ef['ApiKey'] = os.environ['API_KEY']
ef['WorkerKey'] = os.environ['WORKER_KEY']
ef['BaseUrl'] = os.environ['BASE_URL']
ef['PublicUrl'] = os.environ['BASE_URL']
ef['WorkerBaseUrl'] = os.environ['WORKER_URL']
ef['WsUrl'] = os.environ['WS_URL']
vast_key = (ef.get('VastAi') or {}).get('ApiKey') or (data.get('VastAi') or {}).get('ApiKey') or ''
if vast_key:
    ef.setdefault('VastAi', {})['ApiKey'] = vast_key
worker_secret = ef.get('WorkerSecret') or (data.get('Workers') or {}).get('Secret') or (data.get('LoboForge') or {}).get('WorkersSecret') or ''
if worker_secret:
    ef['WorkerSecret'] = worker_secret
hf = ef.get('HuggingFaceToken') or (data.get('HuggingFace') or {}).get('Token') or ''
if hf:
    ef['HuggingFaceToken'] = hf
print(json.dumps(data, separators=(',', ':'), ensure_ascii=False))
")

aws secretsmanager put-secret-value --secret-id "$SECRET_ID" --region "$REGION" --secret-string "$merged" >/dev/null
echo "✓ EventForge secrets merged (ApiKey prefix ${API_KEY:0:8}…, WorkerKey prefix ${WORKER_KEY:0:8}…)"
echo "  BaseUrl=$BASE_URL WorkerBaseUrl=$WORKER_URL"
echo "Restart loboforge ECS service to pick up APP_SECRETS_JSON."
