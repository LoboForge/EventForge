#!/usr/bin/env bash
# Register ECS task definition with explicit EventForge image and roll the service.
# Uses commit-pinned tag (immutable) — never loboforge:eventforge-latest (monorepo).
set -euo pipefail

CLUSTER="${ECS_CLUSTER_NAME:-loboforge}"
SERVICE="${ECS_SERVICE_NAME:-eventforge}"
CONTAINER="${EVENTFORGE_CONTAINER_NAME:-eventforge}"
REGION="${AWS_DEFAULT_REGION:-us-east-2}"
IMAGE_URI="${1:?image URI required}"

step() { echo "▶ $*"; }

if [[ "$IMAGE_URI" != *eventforge-standalone-* ]]; then
  echo "Refusing to deploy non-standalone image: $IMAGE_URI" >&2
  echo "Use loboforge:eventforge-standalone-<commit> (not eventforge-latest)." >&2
  exit 1
fi

step "Resolve current task definition for ${SERVICE}"
CURRENT_ARN=$(aws ecs describe-services \
  --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
  --query 'services[0].taskDefinition' --output text)

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
for k in (
    "taskDefinitionArn", "revision", "status", "requiresAttributes",
    "compatibilities", "registeredAt", "registeredBy",
):
    td.pop(k, None)
updated = False
for c in td.get("containerDefinitions", []):
    if c.get("name") == container:
        c["image"] = image
        updated = True
if not updated:
    raise SystemExit(f"container {container!r} not found in task definition")
with open(path, "w") as f:
    json.dump(td, f)
PY

step "Register new task definition"
NEW_ARN=$(aws ecs register-task-definition \
  --region "$REGION" --cli-input-json "file://${TMP}" \
  --query 'taskDefinition.taskDefinitionArn' --output text)
rm -f "$TMP"
echo "  Registered: $NEW_ARN"

step "Update service (single-task rolling — no overlap)"
aws ecs update-service \
  --cluster "$CLUSTER" \
  --service "$SERVICE" \
  --task-definition "$NEW_ARN" \
  --deployment-configuration "minimumHealthyPercent=0,maximumPercent=100,deploymentCircuitBreaker={enable=true,rollback=true}" \
  --force-new-deployment \
  --region "$REGION" \
  --query 'service.{taskDef:taskDefinition,desired:desiredCount}' \
  --output json

step "Wait for service stable"
aws ecs wait services-stable --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION"
echo "✓ ECS deploy complete: $NEW_ARN"
