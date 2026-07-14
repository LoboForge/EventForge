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

step "Pre-deploy queue persist (flush + guarded S3 backup)"
bash "$(dirname "$0")/eventforge-pre-deploy-persist.sh"

# Record pre-roll task ARN so we don't declare healthy while the old task still answers /health.
OLD_TASK=$(aws ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" \
  --desired-status RUNNING --region "$REGION" --query 'taskArns[0]' --output text 2>/dev/null || true)

step "Update service (desiredCount=1, minHealthy=0, maxPercent=100)"
aws ecs update-service \
  --cluster "$CLUSTER" --service "$SERVICE" \
  --task-definition "$NEW_ARN" --desired-count 1 \
  --deployment-configuration "minimumHealthyPercent=0,maximumPercent=100,deploymentCircuitBreaker={enable=true,rollback=true}" \
  --availability-zone-rebalancing DISABLED \
  --force-new-deployment --region "$REGION" >/dev/null

step "Wait for single new task healthy (up to 5 min)"
for i in $(seq 1 60); do
  DESIRED=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].desiredCount' --output text)
  if [[ "$DESIRED" != "1" ]]; then
    echo "  desiredCount=$DESIRED — restoring to 1" >&2
    aws ecs update-service --cluster "$CLUSTER" --service "$SERVICE" --desired-count 1 --region "$REGION" >/dev/null
  fi

  RUNNING=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].runningCount' --output text)
  DEP_COUNT=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'length(services[0].deployments)' --output text)
  ROLL=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].deployments[0].rolloutState' --output text)
  CUR_TD=$(aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
    --query 'services[0].taskDefinition' --output text)
  CUR_TASK=$(aws ecs list-tasks --cluster "$CLUSTER" --service-name "$SERVICE" \
    --desired-status RUNNING --region "$REGION" --query 'taskArns[0]' --output text 2>/dev/null || true)

  NEW_TASK_UP=0
  if [[ -n "$CUR_TASK" && "$CUR_TASK" != "None" && "$CUR_TASK" != "$OLD_TASK" ]]; then
    NEW_TASK_UP=1
  fi

  if [[ "$RUNNING" == "1" && "$DEP_COUNT" == "1" && "$ROLL" == "COMPLETED" && "$CUR_TD" == "$NEW_ARN" && "$NEW_TASK_UP" == "1" ]] \
      && curl -sf --max-time 10 "$HEALTH_URL" >/dev/null 2>&1; then
    echo "✓ Healthy after ${i}0s — $NEW_ARN (task ${CUR_TASK##*/})"
    exit 0
  fi
  echo "  [$i/60] running=$RUNNING deployments=$DEP_COUNT rollout=$ROLL new_task=$NEW_TASK_UP waiting..."
  sleep 10
done
echo "Deploy timed out waiting for health" >&2
exit 1
