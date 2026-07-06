#!/usr/bin/env bash
# Fix EventForge routing to match www.loboforge.com pattern:
#   - eventforge-aws sidecar only (127.0.0.1:8090)
#   - NO eventforge hostname on loboforge-aws (no IP bridge)
#   - hostname route on eventforge-aws
# DNS must be set separately (Zone DNS Edit): see docs/runbooks/eventforge-dns.md
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGION="${AWS_REGION:-us-east-2}"
CF_ACCOUNT="${CF_ACCOUNT:-6b61869e5064361c73ab91f95d52ee87}"
HOSTNAME="${EVENTFORGE_HOSTNAME:-eventforge.loboforge.com}"
EF_TUNNEL_ID="${EVENTFORGE_TUNNEL_ID:-6541dff9-f29a-4a48-b3d7-c3a489496293}"
LF_TUNNEL_ID="${LOBOFORGE_TUNNEL_ID:-7d16bdae-3019-4e9e-a4ce-8510a9688e8f}"

# shellcheck source=eventforge-tunnel-lib.sh
source "${ROOT}/scripts/eventforge-tunnel-lib.sh"

step() { echo "▶ $*"; }

load_cf_token_from_env_or_secret || {
  echo "ERROR: Set CF_TOKEN or loboforge/cloudflare-api-token" >&2
  exit 1
}

step "Clean loboforge-aws (www, apex, aws only — like production cutover)"
remove_eventforge_from_loboforge_tunnel

step "Remove any loboforge-aws Zero Trust hostname route for ${HOSTNAME}"
routes=$(curl -sf -G "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/zerotrust/routes/hostname" \
  -H "Authorization: Bearer $CF_TOKEN" \
  --data-urlencode "hostname=${HOSTNAME}" \
  | python3 -c "import json,sys; print(' '.join(r['id'] for r in json.load(sys.stdin).get('result',[]) if r.get('tunnel_id')=='${LF_TUNNEL_ID}'))")
for rid in $routes; do
  [[ -z "$rid" ]] && continue
  curl -sf -X DELETE "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/zerotrust/routes/hostname/${rid}" \
    -H "Authorization: Bearer $CF_TOKEN" >/dev/null
  echo "  ✓ deleted loboforge-aws route ${rid}"
done

step "Ensure eventforge-aws tunnel ingress → sidecar"
configure_eventforge_sidecar_tunnel "$EF_TUNNEL_ID"

step "Ensure Zero Trust hostname route on eventforge-aws"
existing_ef=$(curl -sf -G "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/zerotrust/routes/hostname" \
  -H "Authorization: Bearer $CF_TOKEN" \
  --data-urlencode "hostname=${HOSTNAME}" \
  | python3 -c "import json,sys; r=json.load(sys.stdin).get('result',[]); print(r[0]['id'] if r and r[0].get('tunnel_id')=='${EF_TUNNEL_ID}' else '')" 2>/dev/null || true)
if [[ -z "$existing_ef" ]]; then
  curl -sf -X POST "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/zerotrust/routes/hostname" \
    -H "Authorization: Bearer $CF_TOKEN" -H "Content-Type: application/json" \
    -d "{\"hostname\":\"${HOSTNAME}\",\"tunnel_id\":\"${EF_TUNNEL_ID}\",\"comment\":\"EventForge sidecar\"}" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('✓ hostname route' if d.get('success') else d)"
else
  echo "  ✓ hostname route already on eventforge-aws (${existing_ef})"
fi

step "Refresh sidecar connector token"
TOKEN=$(curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/cfd_tunnel/${EF_TUNNEL_ID}/token" \
  -H "Authorization: Bearer $CF_TOKEN" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('result','')); sys.exit(0 if d.get('success') else 1)")
aws secretsmanager put-secret-value --secret-id loboforge/eventforge-cloudflare-tunnel --region "$REGION" \
  --secret-string "$TOKEN" >/dev/null
echo "  ✓ token stored"

step "Restore prod secrets → https://${HOSTNAME}"
EVENT_FORGE_BASE_URL="https://${HOSTNAME}" \
EVENT_FORGE_WORKER_BASE_URL="https://${HOSTNAME}" \
EVENT_FORGE_WS_URL="wss://${HOSTNAME}/v1/ws" \
  bash "${ROOT}/scripts/merge-eventforge-secrets.sh"

step "Redeploy EventForge + loboforge ECS"
aws ecs update-service --cluster loboforge --service eventforge --force-new-deployment --region "$REGION" >/dev/null
aws ecs update-service --cluster loboforge --service loboforge --force-new-deployment --region "$REGION" >/dev/null

echo ""
echo "✓ Tunnel routing fixed (matches www.loboforge.com sidecar pattern)."
echo ""
echo "DNS must match www — pick ONE (not both):"
echo "  CNAME  eventforge → ${EF_TUNNEL_ID}.cfargotunnel.com  (proxied)"
echo "    — same idea as www → ${LF_TUNNEL_ID}.cfargotunnel.com"
echo "  OR Tunnel-type row: eventforge → eventforge-aws (dashboard UI)"
echo ""
echo "Delete any other eventforge DNS row (loboforge-aws tunnel, duplicate CNAME, etc.)."
echo "Verify: curl -sf https://${HOSTNAME}/health"
