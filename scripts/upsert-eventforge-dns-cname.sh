#!/usr/bin/env bash
# Upsert eventforge.loboforge.com CNAME → eventforge-aws sidecar tunnel.
# Requires CF_TOKEN with Zone → DNS → Edit (NOT Account DNS Settings).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=eventforge-tunnel-lib.sh
source "${ROOT}/scripts/eventforge-tunnel-lib.sh"

HOSTNAME="${EVENTFORGE_HOSTNAME:-eventforge.loboforge.com}"
TUNNEL_ID="${EVENTFORGE_TUNNEL_ID:-6541dff9-f29a-4a48-b3d7-c3a489496293}"
TUNNEL_CNAME="${TUNNEL_ID}.cfargotunnel.com"
RECORD_NAME="eventforge"

die() { echo "ERROR: $*" >&2; exit 1; }

load_cf_token_from_env_or_secret || die "Set CF_TOKEN or store token in loboforge/cloudflare-api-token"

curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${CF_ACCOUNT}/tokens/verify" \
  -H "Authorization: Bearer $CF_TOKEN" \
  | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('success') else 1)" \
  || die "Invalid CF_TOKEN"

dns_probe=$(curl -sS -G "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records" \
  -H "Authorization: Bearer $CF_TOKEN" \
  --data-urlencode "name=${HOSTNAME}" \
  --data-urlencode "per_page=1")
if ! echo "$dns_probe" | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('success') else 1)"; then
  echo "ERROR: CF_TOKEN cannot access zone DNS API (code 10000)." >&2
  echo "  Token has Tunnel Edit but needs **Zone → DNS → Edit** on loboforge.com." >&2
  echo "  Cloudflare → My Profile → API Tokens → use template **Edit zone DNS** (or add Zone DNS Edit to cursor_agent)." >&2
  echo "  Then: aws secretsmanager put-secret-value --secret-id loboforge/cloudflare-api-token --region us-east-2 --secret-string 'cfat_...'" >&2
  exit 1
fi

existing=$(echo "$dns_probe" | python3 -c "import json,sys; print(json.load(sys.stdin).get('result',[{}])[0].get('id',''))")
payload=$(python3 -c "import json; print(json.dumps({'type':'CNAME','name':'${RECORD_NAME}','content':'${TUNNEL_CNAME}','proxied':True,'ttl':1}))")

if [[ -n "$existing" ]]; then
  echo "▶ Updating CNAME ${HOSTNAME} → ${TUNNEL_CNAME}"
  curl -sf -X PATCH "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records/${existing}" \
    -H "Authorization: Bearer $CF_TOKEN" -H "Content-Type: application/json" -d "$payload" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('✓', d.get('result',{}).get('name'), '→', d.get('result',{}).get('content')); sys.exit(0 if d.get('success') else 1)"
else
  echo "▶ Creating CNAME ${HOSTNAME} → ${TUNNEL_CNAME}"
  curl -sf -X POST "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records" \
    -H "Authorization: Bearer $CF_TOKEN" -H "Content-Type: application/json" -d "$payload" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print('✓', d.get('result',{}).get('name'), '→', d.get('result',{}).get('content')); sys.exit(0 if d.get('success') else 1)"
fi

echo "▶ Waiting for DNS propagation..."
for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
  if curl -sf --max-time 15 "https://${HOSTNAME}/health" >/dev/null 2>&1; then
    echo "✓ https://${HOSTNAME}/health OK"
    exit 0
  fi
  sleep 5
done

die "DNS record created but /health not OK yet — check eventforge-aws sidecar"
