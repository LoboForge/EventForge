#!/usr/bin/env bash
# Register ECS task definition with explicit EventForge image and roll the service.
# Uses commit-pinned tag (immutable) — never loboforge:eventforge-latest (monorepo).
set -euo pipefail

CLUSTER="${ECS_CLUSTER_NAME:-loboforge}"
SERVICE="${ECS_SERVICE_NAME:-eventforge}"
CONTAINER="${EVENTFORGE_CONTAINER_NAME:-eventforge}"
REGION="${AWS_DEFAULT_REGION:-us-east-2}"
HEALTH_URL="${EVENTFORGE_HEALTH_URL:-https://eventforge.loboforge.com/health}"
IMAGE_URI="${1:?image URI required}"

step() { echo "▶ $*"; }

if [[ "$IMAGE_URI" != *eventforge-standalone-* ]]; then
  echo "Refusing to deploy non-standalone image: $IMAGE_URI" >&2
  exit 1
fi

step "Resolve current task definition for ${SERVICE}"
CURRENT_ARN=$(aws ecs describe-services \
  --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
  --query 'services[0].taskDefinition' --output text)

CURRENT_IMAGE=$(aws ecs describe-task-definition \
  --task-definition "$CURRENT_ARN" --region "$REGION" \
  --query "taskDefinition.containerDefinitions[?name=='${CONTAINER}'].image | [0]" --output text)

if [[ "$CURRENT_IMAGE" == "$IMAGE_URI" ]]; then
  RUNNING=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].runningCount' --output text)
  if [[ "$RUNNING" == "1" ]] && curl -sf --max-time 10 "$HEALTH_URL" >/dev/null; then
    echo "Already running ${IMAGE_URI} and healthy — skipping ECS roll."
    exit 0
  fi
  echo "Image matches but service unhealthy (running=${RUNNING}); forcing redeploy."
fi

step "Clone ${CURRENT_ARN} with image ${IMAGE_URI}"
TMP=$(mktemp)
aws ecs describe-task-definition \
  --task-definition "$CURRENT_ARN" --region "$REGION" \
  --query 'taskDefinition' --output json >"$TMP"

python3 - "$TMP" "$CONTAINER" "$IMAGE_URI" <<'PY'
import json, sys
path, container, image = sys.argv[1:4]
with open(path) as f:
    td = json.load(f)
for k in ("taskDefinitionArn", "revision", "status", "requiresAttributes", "compatibilities", "registeredAt", "registeredBy"):
    td.pop(k, None)
for c in td.get("containerDefinitions", []):
    if c.get("name") == container:
        c["image"] = image
with open(path, "w") as f:
    json.dump(td, f)
PY

NEW_ARN=$(aws ecs register-task-definition \
  --region "$REGION" --cli-input-json "file://${TMP}" \
  --query 'taskDefinition.taskDefinitionArn' --output text)
rm -f "$TMP"
echo "  Registered: $NEW_ARN"

step "Update service"
aws ecs update-service \
  --cluster "$CLUSTER" --service "$SERVICE" \
  --task-definition "$NEW_ARN" --desired-count 1 \
  --force-new-deployment --region "$REGION" >/dev/null

step "Wait for public health (up to 5 min)"
for i in $(seq 1 60); do
  RUNNING=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].runningCount' --output text)
  if [[ "$RUNNING" == "1" ]] && curl -sf --max-time 10 "$HEALTH_URL" >/dev/null 2>&1; then
    echo "✓ Healthy after ${i}0s — $NEW_ARN"
    exit 0
  fi
  echo "  [$i/60] running=$RUNNING waiting for /health..."
  sleep 10
done
echo "Deploy timed out waiting for health" >&2
exit 1
