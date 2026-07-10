#!/usr/bin/env bash
# Flush EventForge in-memory queue to SQLite and upload a guarded S3 snapshot before ECS roll.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/secrets.sh
source "$ROOT/scripts/lib/secrets.sh"

BASE_URL="${EVENTFORGE_URL:-https://eventforge.loboforge.com}"
MIN_JOBS="${EVENTFORGE_PRE_DEPLOY_MIN_JOBS:-25}"
APP_SECRET_ID="${APP_SECRET_ID:-loboforge/app-secrets}"
REGION="${AWS_DEFAULT_REGION:-us-east-2}"

if [[ -z "${EVENT_FORGE_OPS_KEY:-}" ]]; then
  SECRETS_JSON="$(aws secretsmanager get-secret-value \
    --secret-id "$APP_SECRET_ID" --region "$REGION" \
    --query SecretString --output text 2>/dev/null || true)"
  if [[ -n "$SECRETS_JSON" ]]; then
    export APP_SECRETS_JSON="$SECRETS_JSON"
    # shellcheck source=lib/secrets.sh
    source "$ROOT/scripts/lib/secrets.sh"
  fi
fi

OPS_KEY="${EVENT_FORGE_OPS_KEY:?Set EventForge.OpsKey in secrets.local.json, APP_SECRETS_JSON, or Secrets Manager}"

step() { echo "▶ $*"; }
die() { echo "✗ $*" >&2; exit 1; }

step "Flush in-memory queue + S3 backup at ${BASE_URL}"
HTTP_CODE=$(curl -sS --max-time 120 -o /tmp/ef-flush-backup.json -w "%{http_code}" -X POST "${BASE_URL}/v1/ops/persist/flush-backup" \
  -H "X-EventForge-Ops-Key: ${OPS_KEY}" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json" \
  -H "User-Agent: LoboForge-Worker/1.1" \
  -d '{}' || echo "000")

if [[ "$HTTP_CODE" == "404" || "$HTTP_CODE" == "405" || "$HTTP_CODE" == "000" ]]; then
  HEALTH=$(curl -sf --max-time 15 "${BASE_URL}/health" || echo '{}')
  JOBS_NOW=$(echo "$HEALTH" | python3 -c "import json,sys; print(json.load(sys.stdin).get('jobs_total',0))" 2>/dev/null || echo 0)
  if [[ "$JOBS_NOW" -ge "$MIN_JOBS" ]]; then
    die "flush-backup endpoint unavailable (HTTP ${HTTP_CODE}) with ${JOBS_NOW} jobs in queue — deploy new build via hot path or wait for endpoint"
  fi
  echo "⚠ flush-backup endpoint unavailable (HTTP ${HTTP_CODE}); only ${JOBS_NOW} jobs — continuing deploy"
  exit 0
fi

[[ "$HTTP_CODE" == "200" ]] || die "flush-backup failed with HTTP ${HTTP_CODE}: $(cat /tmp/ef-flush-backup.json 2>/dev/null || true)"
RESP=$(cat /tmp/ef-flush-backup.json)

echo "$RESP" | python3 -m json.tool

UPLOADED=$(echo "$RESP" | python3 -c "import json,sys; print('yes' if json.load(sys.stdin).get('backup_uploaded') else 'no')")
SKIPPED=$(echo "$RESP" | python3 -c "import json,sys; print('yes' if json.load(sys.stdin).get('backup_skipped') else 'no')")
JOBS=$(echo "$RESP" | python3 -c "import json,sys; print(json.load(sys.stdin).get('jobs_total',0))")
LOCAL_JOBS=$(echo "$RESP" | python3 -c "import json,sys; print(json.load(sys.stdin).get('local_job_count',0))")

if [[ "$UPLOADED" == "yes" ]]; then
  echo "✓ S3 snapshot uploaded (${LOCAL_JOBS} jobs in SQLite)"
  exit 0
fi

if [[ "$SKIPPED" == "yes" && "$JOBS" -ge "$MIN_JOBS" ]]; then
  die "Refusing deploy: ${JOBS} jobs in memory but S3 backup was skipped (would lose queue on restart). Check backup_skip_reason in response."
fi

if [[ "$JOBS" -lt "$MIN_JOBS" ]]; then
  echo "⚠ Low job count (${JOBS}) — backup skip allowed for deploy"
  exit 0
fi

echo "✓ Pre-deploy persist check complete"
