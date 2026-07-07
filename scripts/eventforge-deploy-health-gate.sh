#!/usr/bin/env bash
# Post-deploy smoke checks for EventForge (public URL).
set -euo pipefail

BASE_URL="${1:-https://eventforge.loboforge.com}"
EXPECT_COMMIT="${2:-}"

step() { echo "▶ $*"; }

step "GET ${BASE_URL}/health"
health=$(curl -sf --max-time 30 "${BASE_URL}/health")
echo "$health" | grep -q '"ok":true' || { echo "health check failed: $health"; exit 1; }

step "GET ${BASE_URL}/ (SPA shell)"
html=$(curl -sf --max-time 30 "${BASE_URL}/")
echo "$html" | grep -q 'assets/index-' || { echo "index.html missing vite assets"; exit 1; }

step "GET ${BASE_URL}/agent/provision_worker.sh"
curl -sf --max-time 30 "${BASE_URL}/agent/provision_worker.sh" | head -1 | grep -q '#!/' \
  || { echo "agent bootstrap script missing"; exit 1; }

if [[ -n "$EXPECT_COMMIT" ]]; then
  step "Verify standalone image tag eventforge-standalone-${EXPECT_COMMIT} exists in ECR"
  aws ecr describe-images \
    --repository-name loboforge \
    --image-ids "imageTag=eventforge-standalone-${EXPECT_COMMIT}" \
    --region "${AWS_DEFAULT_REGION:-us-east-2}" >/dev/null
fi

echo "✓ EventForge health gate passed"
