#!/usr/bin/env bash
# DEPRECATED — use setup-eventforge-tunnel.sh (eventforge-aws sidecar only).
#
# This wrapper only removes stale eventforge.loboforge.com rules from loboforge-aws.
# It does NOT resolve task IPs or write private/public IP bridges.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=eventforge-tunnel-lib.sh
source "${ROOT}/scripts/eventforge-tunnel-lib.sh"

MERGE_SECRETS=0
for arg in "$@"; do
  case "$arg" in
    --merge-secrets) MERGE_SECRETS=1 ;;
    --ip|--ip-only|--legacy-ip)
      echo "ERROR: IP bridge routing is disabled. Use ./scripts/setup-eventforge-tunnel.sh" >&2
      exit 1
      ;;
  esac
done

echo "⚠ update-eventforge-cloudflare.sh is deprecated."
echo "  Preferred: ./scripts/setup-eventforge-tunnel.sh --merge-secrets"
echo ""
echo "Removing eventforge hostname from loboforge-aws tunnel..."
remove_eventforge_from_loboforge_tunnel

if [[ "$MERGE_SECRETS" == "1" ]]; then
  EVENT_FORGE_BASE_URL="https://${EVENTFORGE_HOSTNAME}" \
  EVENT_FORGE_WORKER_BASE_URL="https://${EVENTFORGE_HOSTNAME}" \
  EVENT_FORGE_WS_URL="wss://${EVENTFORGE_HOSTNAME}/v1/ws" \
    bash "${ROOT}/scripts/merge-eventforge-secrets.sh"
  aws ecs update-service --cluster loboforge --service loboforge --force-new-deployment --region "${AWS_REGION:-us-east-2}" >/dev/null
fi

echo ""
echo "Verify (after DNS CNAME points at eventforge-aws): curl -sf https://${EVENTFORGE_HOSTNAME}/health"
