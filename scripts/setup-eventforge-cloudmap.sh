#!/usr/bin/env bash
# Register EventForge with AWS Cloud Map so eventforge.loboforge.local tracks the
# current Fargate task IP for in-VPC callers (LoboForge API container).
#
# Public URL uses eventforge-aws cloudflared sidecar — NOT loboforge-aws tunnel bridge.
# Idempotent — safe to run from bootstrap or CI.
set -euo pipefail

REGION="${AWS_REGION:-us-east-2}"
VPC="${LOBFORGE_VPC:-vpc-020361e2441bef993}"
NAMESPACE_NAME="${EVENTFORGE_NAMESPACE:-loboforge.local}"
SERVICE_NAME="${EVENTFORGE_SD_SERVICE:-eventforge}"
ECS_CLUSTER="${ECS_CLUSTER:-loboforge}"
ECS_SERVICE="${ECS_SERVICE:-eventforge}"

step() { echo "▶ $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

step "Resolving Cloud Map namespace ${NAMESPACE_NAME}..."
NS_ID=$(aws servicediscovery list-namespaces --region "$REGION" \
  --query "Namespaces[?Name=='${NAMESPACE_NAME}'].Id | [0]" --output text 2>/dev/null || true)
if [[ -z "$NS_ID" || "$NS_ID" == "None" ]]; then
  step "Creating private DNS namespace ${NAMESPACE_NAME} in ${VPC}"
  NS_ID=$(aws servicediscovery create-private-dns-namespace \
    --name "$NAMESPACE_NAME" \
    --vpc "$VPC" \
    --region "$REGION" \
    --query 'OperationId' --output text)
  # create-private-dns-namespace is async — poll until namespace exists
  for _ in $(seq 1 30); do
    NS_ID=$(aws servicediscovery list-namespaces --region "$REGION" \
      --query "Namespaces[?Name=='${NAMESPACE_NAME}'].Id | [0]" --output text 2>/dev/null || true)
    [[ -n "$NS_ID" && "$NS_ID" != "None" ]] && break
    sleep 2
  done
fi
[[ -n "$NS_ID" && "$NS_ID" != "None" ]] || die "Cloud Map namespace ${NAMESPACE_NAME} not found"
echo "  Namespace: ${NS_ID}"

step "Resolving Cloud Map service ${SERVICE_NAME}..."
SD_ARN=$(aws servicediscovery list-services --region "$REGION" \
  --filters Name=NAMESPACE_ID,Values="$NS_ID",Condition=EQ \
  --query "Services[?Name=='${SERVICE_NAME}'].Arn | [0]" --output text 2>/dev/null || true)
if [[ -z "$SD_ARN" || "$SD_ARN" == "None" ]]; then
  step "Creating Cloud Map service ${SERVICE_NAME}"
  SD_ARN=$(aws servicediscovery create-service \
    --name "$SERVICE_NAME" \
    --namespace-id "$NS_ID" \
    --dns-config "NamespaceId=${NS_ID},DnsRecords=[{Type=A,TTL=15}]" \
    --health-check-custom-config FailureThreshold=1 \
    --region "$REGION" \
    --query 'Service.Arn' --output text)
fi
echo "  Service: ${SD_ARN}"

if ! aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --region "$REGION" \
  --query 'services[0].status' --output text 2>/dev/null | grep -q ACTIVE; then
  echo "⚠ ECS service ${ECS_CLUSTER}/${ECS_SERVICE} not active — skip service registry attach"
  echo "  Re-run after bootstrap-eventforge-ecs.sh"
  exit 0
fi

CURRENT_REG=$(aws ecs describe-services --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --region "$REGION" \
  --query 'services[0].serviceRegistries[0].registryArn' --output text 2>/dev/null || true)

if [[ "$CURRENT_REG" == "$SD_ARN" ]]; then
  echo "✓ ${ECS_SERVICE} already registered with Cloud Map (${NAMESPACE_NAME}/${SERVICE_NAME})"
else
  step "Attaching Cloud Map registry to ECS service ${ECS_SERVICE}"
  aws ecs update-service \
    --cluster "$ECS_CLUSTER" \
    --service "$ECS_SERVICE" \
    --service-registries "registryArn=${SD_ARN},containerName=eventforge,containerPort=8090" \
    --region "$REGION" >/dev/null
  step "Waiting for service stable after Cloud Map attach..."
  aws ecs wait services-stable --cluster "$ECS_CLUSTER" --services "$ECS_SERVICE" --region "$REGION"
  echo "✓ Cloud Map registered — eventforge.${NAMESPACE_NAME}:8090 tracks current task"
fi
