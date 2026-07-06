#!/usr/bin/env bash
# One-time: dedicated Cloudflare tunnel for EventForge (matches loboforge sidecar pattern).
#
# Creates tunnel eventforge-aws (or reuses), stores connector token in Secrets Manager,
# sets ingress eventforge.loboforge.com → http://127.0.0.1:8090, upserts DNS CNAME.
# After this + bootstrap-eventforge-ecs.sh (cloudflared sidecar), deploys need no CF updates.
#
# Requires CF_TOKEN (Account Tunnel Edit + Zone DNS Edit) or loboforge/cloudflare-api-token.
# Auth: API token only (Bearer). Never cloudflared login / OAuth.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGION="${AWS_REGION:-us-east-2}"
ACCOUNT="${AWS_ACCOUNT_ID:-994185520581}"
CF_ACCOUNT="${CF_ACCOUNT:-6b61869e5064361c73ab91f95d52ee87}"
CF_ZONE="${CF_ZONE:-e3f0fced3bd9dc85db95141ad631ae4b}"
HOSTNAME="${EVENTFORGE_HOSTNAME:-eventforge.loboforge.com}"
TUNNEL_NAME="${EVENTFORGE_TUNNEL_NAME:-eventforge-aws}"
EVENTFORGE_TUNNEL_ID="${EVENTFORGE_TUNNEL_ID:-6541dff9-f29a-4a48-b3d7-c3a489496293}"
ORIGIN="http://127.0.0.1:8090"
CF_API_SECRET="${CF_API_SECRET:-loboforge/cloudflare-api-token}"
EF_TUNNEL_SECRET="${EF_TUNNEL_SECRET:-loboforge/eventforge-cloudflare-tunnel}"
MERGE_SECRETS=0

# shellcheck source=eventforge-tunnel-lib.sh
source "${ROOT}/scripts/eventforge-tunnel-lib.sh"

for arg in "$@"; do
  case "$arg" in
    --merge-secrets) MERGE_SECRETS=1 ;;
  esac
done

step() { echo "▶ $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

load_cf_token() {
  [[ -n "${CF_TOKEN:-}" ]] && return 0
  local raw
  raw=$(aws secretsmanager get-secret-value --secret-id "$CF_API_SECRET" --region "$REGION" \
    --query SecretString --output text 2>/dev/null || true)
  [[ -n "$raw" && "$raw" != "None" ]] || return 1
  if [[ "$raw" == cfat_* ]]; then
    CF_TOKEN="$raw"
  else
    CF_TOKEN=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('token') or d.get('CF_TOKEN') or '')" <<<"$raw" 2>/dev/null || true)
  fi
  [[ -n "${CF_TOKEN:-}" ]]
}

load_cf_token || die "Set CF_TOKEN or store API token in ${CF_API_SECRET}"

curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/tokens/verify" \
  -H "Authorization: Bearer $CF_TOKEN" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('success') else 1)" \
  || die "Invalid CF_TOKEN"

step "Resolving tunnel ${TUNNEL_NAME}..."
TUNNEL_ID=$(curl -sf -G "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/cfd_tunnel" \
  -H "Authorization: Bearer $CF_TOKEN" \
  --data-urlencode "name=${TUNNEL_NAME}" \
  | python3 -c "import json,sys; r=json.load(sys.stdin).get('result',[]); print(r[0]['id'] if r else '')")

if [[ -z "$TUNNEL_ID" ]]; then
  TUNNEL_ID="$EVENTFORGE_TUNNEL_ID"
  step "Using known tunnel ID ${TUNNEL_ID}"
fi

if [[ -z "$TUNNEL_ID" ]]; then
  step "Creating tunnel ${TUNNEL_NAME}"
  TUNNEL_ID=$(curl -sf -X POST "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/cfd_tunnel" \
    -H "Authorization: Bearer $CF_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{\"name\":\"${TUNNEL_NAME}\",\"tunnel_secret\":\"$(openssl rand -base64 32)\"}" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('result',{}).get('id','')); sys.exit(0 if d.get('success') else 1)")
fi
[[ -n "$TUNNEL_ID" ]] || die "Could not create or find tunnel ${TUNNEL_NAME}"
echo "  Tunnel ID: ${TUNNEL_ID}"

step "Issuing connector token → Secrets Manager (${EF_TUNNEL_SECRET})"
TOKEN=$(curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/cfd_tunnel/${TUNNEL_ID}/token" \
  -H "Authorization: Bearer $CF_TOKEN" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('result','')); sys.exit(0 if d.get('success') else 1)")

if aws secretsmanager describe-secret --secret-id "$EF_TUNNEL_SECRET" --region "$REGION" >/dev/null 2>&1; then
  aws secretsmanager put-secret-value --secret-id "$EF_TUNNEL_SECRET" --region "$REGION" \
    --secret-string "$TOKEN" >/dev/null
else
  aws secretsmanager create-secret --name "$EF_TUNNEL_SECRET" --region "$REGION" \
    --secret-string "$TOKEN" >/dev/null
fi
echo "  ✓ Token stored"

TUNNEL_CNAME="${TUNNEL_ID}.cfargotunnel.com"

step "Tunnel ingress ${HOSTNAME} → ${ORIGIN}"
configure_eventforge_sidecar_tunnel "$TUNNEL_ID"

step "DNS CNAME eventforge → ${TUNNEL_CNAME}"
dns_ok=0
existing=$(curl -sf -G "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records" \
  -H "Authorization: Bearer $CF_TOKEN" \
  --data-urlencode "name=${HOSTNAME}" \
  --data-urlencode "type=CNAME" \
  | python3 -c "import json,sys; print(json.load(sys.stdin).get('result',[{}])[0].get('id',''))" 2>/dev/null || true)

payload="{\"type\":\"CNAME\",\"name\":\"eventforge\",\"content\":\"${TUNNEL_CNAME}\",\"proxied\":true,\"ttl\":1}"
if [[ -n "$existing" ]]; then
  if curl -sf -X PATCH "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records/${existing}" \
    -H "Authorization: Bearer $CF_TOKEN" -H "Content-Type: application/json" -d "$payload" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('✓', d.get('result',{}).get('name')); sys.exit(0 if d.get('success') else 1)"; then
    dns_ok=1
  fi
else
  if curl -sf -X POST "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records" \
    -H "Authorization: Bearer $CF_TOKEN" -H "Content-Type: application/json" -d "$payload" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('✓', d.get('result',{}).get('name')); sys.exit(0 if d.get('success') else 1)"; then
    dns_ok=1
  fi
fi
if [[ "$dns_ok" != "1" ]]; then
  echo "  ⚠ DNS upsert failed (CF_TOKEN needs Zone DNS Edit for loboforge.com)"
  echo ""
  echo "  One-time dashboard fix (1–2 clicks):"
  echo "    1. Cloudflare → loboforge.com → DNS"
  echo "    2. Edit or add CNAME: Name eventforge → Target ${TUNNEL_CNAME} → Proxied ON"
  echo "    (Delete any Tunnel-type row that routes eventforge to loboforge-aws.)"
  echo ""
  echo "  Keeping loboforge-aws IP bridge until DNS points at ${TUNNEL_CNAME}."
  bash "${ROOT}/scripts/sync-eventforge-tunnel-bridge.sh" || true
else
  step "Waiting for sidecar route (DNS may take ~30s)..."
  for _ in 1 2 3 4 5 6; do
    if curl -sf --max-time 15 "https://${HOSTNAME}/health" >/dev/null; then
      break
    fi
    sleep 10
  done
  if curl -sf --max-time 15 "https://${HOSTNAME}/health" >/dev/null; then
    step "Removing ${HOSTNAME} from loboforge-aws tunnel (sidecar-only routing)"
    remove_eventforge_from_loboforge_tunnel
  else
    echo "  ⚠ Health check failed after DNS cutover — loboforge-aws bridge left in place"
    bash "${ROOT}/scripts/sync-eventforge-tunnel-bridge.sh" || true
  fi
fi

if [[ "$MERGE_SECRETS" == "1" ]]; then
  step "Merging EventForge URLs in app-secrets"
  EVENT_FORGE_BASE_URL="https://${HOSTNAME}" \
  EVENT_FORGE_WORKER_BASE_URL="https://${HOSTNAME}" \
  EVENT_FORGE_WS_URL="wss://${HOSTNAME}/v1/ws" \
    bash "${ROOT}/scripts/merge-eventforge-secrets.sh"
  aws ecs update-service --cluster loboforge --service loboforge --force-new-deployment --region "$REGION" >/dev/null
fi

step "Redeploy EventForge ECS (registers task def with cloudflared sidecar)"
bash "${ROOT}/scripts/bootstrap-eventforge-ecs.sh"

echo ""
echo "✓ EventForge tunnel ready — ${HOSTNAME} → sidecar localhost:8090"
echo "  Verify: curl -sf https://${HOSTNAME}/health"
