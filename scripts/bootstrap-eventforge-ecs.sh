#!/usr/bin/env bash
# One-time: register EventForge ECS task + service (desiredCount=1) on loboforge cluster.
# Includes cloudflared sidecar (dedicated tunnel token) — same pattern as loboforge.com.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGION="${AWS_REGION:-us-east-2}"
ACCOUNT="${AWS_ACCOUNT_ID:-994185520581}"
CLUSTER="loboforge"
SERVICE="eventforge"
FAMILY="eventforge"
ECR_REPO="${ACCOUNT}.dkr.ecr.${REGION}.amazonaws.com/loboforge"
IMAGE_TAG="${IMAGE_TAG:-eventforge-standalone-latest}"
SUBNET="${SUBNET:-subnet-04982efc4adf72e1e}"
TASK_SG="${TASK_SG:-sg-08ad7b44cee5697e8}"
EXEC_ROLE="arn:aws:iam::${ACCOUNT}:role/loboforge-ecs-execution"
TASK_ROLE="arn:aws:iam::${ACCOUNT}:role/loboforge-ecs-task"
LOG_GROUP="/ecs/loboforge"
APP_SECRET_ARN="${APP_SECRET_ARN:-arn:aws:secretsmanager:${REGION}:${ACCOUNT}:secret:loboforge/app-secrets-IuHDcg}"
EF_TUNNEL_SECRET_ID="${EF_TUNNEL_SECRET_ID:-loboforge/eventforge-cloudflare-tunnel}"
EF_TUNNEL_SECRET_ARN=""

step() { echo "▶ $*"; }

resolve_secret_arn() {
  aws secretsmanager describe-secret --secret-id "$1" --region "$REGION" \
    --query ARN --output text 2>/dev/null || true
}

aws logs create-log-group --log-group-name "$LOG_GROUP" --region "$REGION" 2>/dev/null || true

step "Login ECR + resolve image tag"
aws ecr get-login-password --region "$REGION" | docker login --username AWS --password-stdin "${ACCOUNT}.dkr.ecr.${REGION}.amazonaws.com" 2>/dev/null || true
if ! aws ecr describe-images --repository-name loboforge --image-ids imageTag=eventforge-standalone-latest --region "$REGION" >/dev/null 2>&1; then
  IMAGE_TAG=$(aws ecr describe-images --repository-name loboforge --region "$REGION" --query 'sort_by(imageDetails,& imagePushedAt)[-1].imageTags[0]' --output text 2>/dev/null || echo latest)
fi
echo "  Using image ${ECR_REPO}:${IMAGE_TAG}"

HAS_TUNNEL=0
if aws secretsmanager get-secret-value --secret-id "$EF_TUNNEL_SECRET_ID" --region "$REGION" \
  --query SecretString --output text >/dev/null 2>&1; then
  HAS_TUNNEL=1
  EF_TUNNEL_SECRET_ARN=$(resolve_secret_arn "$EF_TUNNEL_SECRET_ID")
  echo "  EventForge tunnel token: ${EF_TUNNEL_SECRET_ARN}"
else
  echo "  ⚠ No ${EF_TUNNEL_SECRET_ID} — run scripts/setup-eventforge-tunnel.sh first (app-only task until then)"
fi

tmp=$(mktemp)
if [[ "$HAS_TUNNEL" == "1" ]]; then
cat >"$tmp" <<EOF
{
  "family": "${FAMILY}",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "1024",
  "memory": "2048",
  "executionRoleArn": "${EXEC_ROLE}",
  "taskRoleArn": "${TASK_ROLE}",
  "containerDefinitions": [
    {
      "name": "eventforge",
      "image": "${ECR_REPO}:${IMAGE_TAG}",
      "essential": true,
      "portMappings": [{ "containerPort": 8090, "protocol": "tcp" }],
      "environment": [
        { "name": "AWS_DEFAULT_REGION", "value": "${REGION}" },
        { "name": "ASPNETCORE_ENVIRONMENT", "value": "Production" },
        { "name": "ASPNETCORE_URLS", "value": "http://0.0.0.0:8090" }
      ],
      "secrets": [
        { "name": "APP_SECRETS_JSON", "valueFrom": "${APP_SECRET_ARN}" }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "${LOG_GROUP}",
          "awslogs-region": "${REGION}",
          "awslogs-stream-prefix": "eventforge-app"
        }
      }
    },
    {
      "name": "cloudflared",
      "image": "cloudflare/cloudflared:latest",
      "essential": true,
      "dependsOn": [{ "containerName": "eventforge", "condition": "START" }],
      "command": ["tunnel", "--no-autoupdate", "run"],
      "secrets": [
        { "name": "TUNNEL_TOKEN", "valueFrom": "${EF_TUNNEL_SECRET_ARN}" }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "${LOG_GROUP}",
          "awslogs-region": "${REGION}",
          "awslogs-stream-prefix": "eventforge-tunnel"
        }
      }
    }
  ]
}
EOF
else
cat >"$tmp" <<EOF
{
  "family": "${FAMILY}",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "1024",
  "memory": "2048",
  "executionRoleArn": "${EXEC_ROLE}",
  "taskRoleArn": "${TASK_ROLE}",
  "containerDefinitions": [
    {
      "name": "eventforge",
      "image": "${ECR_REPO}:${IMAGE_TAG}",
      "essential": true,
      "portMappings": [{ "containerPort": 8090, "protocol": "tcp" }],
      "environment": [
        { "name": "AWS_DEFAULT_REGION", "value": "${REGION}" },
        { "name": "ASPNETCORE_ENVIRONMENT", "value": "Production" },
        { "name": "ASPNETCORE_URLS", "value": "http://0.0.0.0:8090" }
      ],
      "secrets": [
        { "name": "APP_SECRETS_JSON", "valueFrom": "${APP_SECRET_ARN}" }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "${LOG_GROUP}",
          "awslogs-region": "${REGION}",
          "awslogs-stream-prefix": "eventforge-app"
        }
      }
    }
  ]
}
EOF
fi

TASK_ARN=$(aws ecs register-task-definition --region "$REGION" --cli-input-json "file://${tmp}" \
  --query 'taskDefinition.taskDefinitionArn' --output text)
rm -f "$tmp"
echo "  Task: $TASK_ARN"

if aws ecs describe-services --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION" \
  --query 'services[?status==`ACTIVE`].serviceName' --output text 2>/dev/null | grep -q "$SERVICE"; then
  step "Updating existing ${SERVICE} service"
  aws ecs update-service --cluster "$CLUSTER" --service "$SERVICE" --task-definition "$TASK_ARN" \
    --desired-count 1 --availability-zone-rebalancing DISABLED \
    --deployment-configuration "minimumHealthyPercent=0,maximumPercent=100,deploymentCircuitBreaker={enable=true,rollback=true}" \
    --force-new-deployment --region "$REGION" >/dev/null
else
  step "Creating ${SERVICE} service (desiredCount=1)"
  aws ecs create-service \
    --cluster "$CLUSTER" \
    --service-name "$SERVICE" \
    --task-definition "$TASK_ARN" \
    --desired-count 1 \
    --launch-type FARGATE \
    --availability-zone-rebalancing DISABLED \
    --deployment-configuration "minimumHealthyPercent=0,maximumPercent=100,deploymentCircuitBreaker={enable=true,rollback=true}" \
    --network-configuration "awsvpcConfiguration={subnets=[${SUBNET}],securityGroups=[${TASK_SG}],assignPublicIp=ENABLED}" \
    --region "$REGION" >/dev/null
fi

step "Waiting for service stable..."
aws ecs wait services-stable --cluster "$CLUSTER" --services "$SERVICE" --region "$REGION"

# Cloud Map optional — in-VPC DNS for loboforge API (does not affect public tunnel)
bash "${ROOT}/scripts/setup-eventforge-cloudmap.sh" 2>/dev/null || \
  echo "  (Cloud Map skipped — needs servicediscovery IAM; public URL uses sidecar tunnel)"

if [[ "$HAS_TUNNEL" == "1" ]]; then
  step "Syncing EventForge public URL (bridge until DNS on eventforge-aws CNAME)"
  bash "${ROOT}/scripts/sync-eventforge-tunnel-bridge.sh" || \
    echo "  ⚠ Bridge sync failed — run: ./scripts/sync-eventforge-tunnel-bridge.sh"
  echo "✓ EventForge running with cloudflared sidecar — https://eventforge.loboforge.com survives deploys"
else
  echo "✓ EventForge ECS service ready."
  echo "  Next: CF_TOKEN=… ./scripts/setup-eventforge-tunnel.sh --merge-secrets"
fi
