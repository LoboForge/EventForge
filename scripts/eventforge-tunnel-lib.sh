#!/usr/bin/env bash
# Shared Cloudflare helpers for EventForge sidecar tunnel (eventforge-aws only).
set -euo pipefail

EVENTFORGE_TUNNEL_ID="${EVENTFORGE_TUNNEL_ID:-6541dff9-f29a-4a48-b3d7-c3a489496293}"
EVENTFORGE_TUNNEL_NAME="${EVENTFORGE_TUNNEL_NAME:-eventforge-aws}"
LOBOFORGE_TUNNEL_ID="${LOBOFORGE_TUNNEL_ID:-7d16bdae-3019-4e9e-a4ce-8510a9688e8f}"
LOBOFORGE_ORIGIN="${LOBOFORGE_ORIGIN:-http://127.0.0.1:8080}"
EVENTFORGE_HOSTNAME="${EVENTFORGE_HOSTNAME:-eventforge.loboforge.com}"
EVENTFORGE_SIDECAR_ORIGIN="${EVENTFORGE_SIDECAR_ORIGIN:-http://127.0.0.1:8090}"
EVENTFORGE_APP_PORT="${EVENTFORGE_APP_PORT:-8090}"
CF_ZONE="${CF_ZONE:-e3f0fced3bd9dc85db95141ad631ae4b}"
CF_ACCOUNT="${CF_ACCOUNT:-6b61869e5064361c73ab91f95d52ee87}"
ECS_CLUSTER="${ECS_CLUSTER:-loboforge}"
ECS_SERVICE="${ECS_SERVICE:-eventforge}"

load_cf_token_from_env_or_secret() {
  local region="${AWS_REGION:-us-east-2}"
  local secret="${CF_API_SECRET:-loboforge/cloudflare-api-token}"
  [[ -n "${CF_TOKEN:-}" ]] && return 0
  local raw
  raw=$(aws secretsmanager get-secret-value --secret-id "$secret" --region "$region" \
    --query SecretString --output text 2>/dev/null || true)
  [[ -n "$raw" && "$raw" != "None" ]] || return 1
  if [[ "$raw" == cfat_* ]]; then
    CF_TOKEN="$raw"
  else
    CF_TOKEN=$(python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('token') or d.get('CF_TOKEN') or '')" <<<"$raw" 2>/dev/null || true)
  fi
  [[ -n "${CF_TOKEN:-}" ]]
}

remove_eventforge_from_loboforge_tunnel() {
  local account="${CF_ACCOUNT:-6b61869e5064361c73ab91f95d52ee87}"
  load_cf_token_from_env_or_secret || {
    echo "⚠ Skipping loboforge-aws cleanup — no CF_TOKEN (Tunnel Edit required)" >&2
    return 0
  }

  curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${account}/tokens/verify" \
    -H "Authorization: Bearer $CF_TOKEN" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('success') else 1)" \
    || return 0

  curl -sf -X PUT "https://api.cloudflare.com/client/v4/accounts/${account}/cfd_tunnel/${LOBOFORGE_TUNNEL_ID}/configurations" \
    -H "Authorization: Bearer $CF_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{
      \"config\": {
        \"ingress\": [
          {\"hostname\": \"www.loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"hostname\": \"loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"hostname\": \"aws.loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"service\": \"http_status:404\"}
        ]
      }
    }" | python3 -c "
import json,sys
d=json.load(sys.stdin)
ok=d.get('success') is True
print('✓ loboforge-aws tunnel: no eventforge ingress' if ok else '⚠ loboforge-aws cleanup:', d)
sys.exit(0 if ok else 0)
"
}

configure_eventforge_sidecar_tunnel() {
  local account="${CF_ACCOUNT:-6b61869e5064361c73ab91f95d52ee87}"
  local tunnel_id="${1:-$EVENTFORGE_TUNNEL_ID}"
  curl -sf -X PUT "https://api.cloudflare.com/client/v4/accounts/${account}/cfd_tunnel/${tunnel_id}/configurations" \
    -H "Authorization: Bearer $CF_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{
      \"config\": {
        \"ingress\": [
          {\"hostname\": \"${EVENTFORGE_HOSTNAME}\", \"service\": \"${EVENTFORGE_SIDECAR_ORIGIN}\"},
          {\"service\": \"http_status:404\"}
        ]
      }
    }" | python3 -c "
import json,sys
d=json.load(sys.stdin)
die=d.get('success') is not True
print('✓ eventforge-aws ingress → sidecar' if not die else d)
sys.exit(1 if die else 0)
"
}

resolve_eventforge_task_public_ip() {
  local region="${AWS_REGION:-us-east-2}"
  local task eni ip
  task=$(aws ecs list-tasks --cluster "$ECS_CLUSTER" --service-name "$ECS_SERVICE" --region "$region" \
    --desired-status RUNNING --query 'taskArns[0]' --output text)
  [[ -n "$task" && "$task" != "None" ]] || return 1
  eni=$(aws ecs describe-tasks --cluster "$ECS_CLUSTER" --tasks "$task" --region "$region" \
    --query 'tasks[0].attachments[0].details[?name==`networkInterfaceId`].value | [0]' \
    --output text)
  [[ -n "$eni" && "$eni" != "None" ]] || return 1
  ip=$(aws ec2 describe-network-interfaces --network-interface-ids "$eni" --region "$region" \
    --query 'NetworkInterfaces[0].Association.PublicIp' --output text)
  [[ -n "$ip" && "$ip" != "None" ]] || return 1
  echo "$ip"
}

eventforge_dns_on_dedicated_tunnel() {
  [[ "${SKIP_DNS_CHECK:-0}" == "1" ]] && return 1
  load_cf_token_from_env_or_secret || return 1

  local cname dedicated_cname
  cname=$(curl -sf -G "https://api.cloudflare.com/client/v4/zones/${CF_ZONE}/dns_records" \
    -H "Authorization: Bearer $CF_TOKEN" \
    --data-urlencode "name=${EVENTFORGE_HOSTNAME}" \
    --data-urlencode "type=CNAME" \
    | python3 -c "import json,sys; print(json.load(sys.stdin).get('result',[{}])[0].get('content',''))" 2>/dev/null || true)
  [[ -n "$cname" ]] || return 1
  dedicated_cname="${EVENTFORGE_TUNNEL_ID}.cfargotunnel.com"
  [[ "$cname" == "$dedicated_cname" ]]
}

# Fallback when DNS is not CNAME'd to eventforge-aws: route via loboforge-aws to task public IP.
sync_eventforge_tunnel_bridge() {
  local dry_run="${1:-0}"
  local account="${CF_ACCOUNT}"
  local public_ip origin current

  public_ip=$(resolve_eventforge_task_public_ip) || {
    echo "ERROR: No running ${ECS_SERVICE} task in ${ECS_CLUSTER}" >&2
    return 1
  }
  origin="http://${public_ip}:${EVENTFORGE_APP_PORT}"
  echo "  EventForge bridge origin: ${origin}"

  if eventforge_dns_on_dedicated_tunnel; then
    echo "✓ DNS on ${EVENTFORGE_TUNNEL_NAME} — bridge sync not needed"
    return 0
  fi

  if [[ "$dry_run" == "1" ]]; then
    echo "[dry-run] Would update loboforge-aws (${LOBOFORGE_TUNNEL_ID}):"
    echo "  ${EVENTFORGE_HOSTNAME} → ${origin}"
    return 0
  fi

  load_cf_token_from_env_or_secret || {
    echo "ERROR: Set CF_TOKEN or store token in loboforge/cloudflare-api-token" >&2
    return 1
  }

  curl -sf -X GET "https://api.cloudflare.com/client/v4/accounts/${account}/tokens/verify" \
    -H "Authorization: Bearer $CF_TOKEN" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if d.get('success') else 1)" \
    || return 1

  current=$(curl -sf -G "https://api.cloudflare.com/client/v4/accounts/${account}/cfd_tunnel/${LOBOFORGE_TUNNEL_ID}/configurations" \
    -H "Authorization: Bearer $CF_TOKEN" \
    | python3 -c "
import json,sys
cfg=json.load(sys.stdin).get('result',{}).get('config',{})
for rule in cfg.get('ingress',[]):
    if rule.get('hostname')=='${EVENTFORGE_HOSTNAME}':
        print(rule.get('service',''))
        break
" 2>/dev/null || true)

  if [[ "$current" == "$origin" ]]; then
    echo "✓ loboforge-aws bridge already at ${origin}"
    return 0
  fi

  curl -sf -X PUT "https://api.cloudflare.com/client/v4/accounts/${account}/cfd_tunnel/${LOBOFORGE_TUNNEL_ID}/configurations" \
    -H "Authorization: Bearer $CF_TOKEN" \
    -H "Content-Type: application/json" \
    -d "{
      \"config\": {
        \"ingress\": [
          {\"hostname\": \"www.loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"hostname\": \"loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"hostname\": \"aws.loboforge.com\", \"service\": \"${LOBOFORGE_ORIGIN}\"},
          {\"hostname\": \"${EVENTFORGE_HOSTNAME}\", \"service\": \"${origin}\"},
          {\"service\": \"http_status:404\"}
        ]
      }
    }" | python3 -c "
import json,sys
d=json.load(sys.stdin)
die=d.get('success') is not True
print('✓ loboforge-aws bridge updated' if not die else d)
sys.exit(1 if die else 0)
"

  if curl -sf --max-time 20 "https://${EVENTFORGE_HOSTNAME}/health" >/dev/null; then
    echo "✓ https://${EVENTFORGE_HOSTNAME}/health OK"
  else
    echo "⚠ Health check pending — tunnel may need ~30s" >&2
    return 1
  fi
}
