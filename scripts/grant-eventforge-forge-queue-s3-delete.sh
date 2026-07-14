#!/usr/bin/env bash
# Grant EventForge ECS task role permission to list/delete job artifacts on the forge-queue bucket.
# Required by POST /v1/ops/jobs/purge-queued with delete_s3:true (ListObjectsV2 + DeleteObjects).
#
# Without this, ArtifactStore.DeleteJobArtifacts* fails with AccessDenied and the purge HTTP
# call returns 5xx mid-batch. Put/Get alone (legacy ForgeQueueProducerInline) is not enough.
set -euo pipefail

REGION="${AWS_REGION:-us-east-2}"
ACCOUNT="${AWS_ACCOUNT_ID:-994185520581}"
ROLE_NAME="${EVENTFORGE_TASK_ROLE_NAME:-loboforge-ecs-task}"
POLICY_NAME="${EVENTFORGE_FORGE_QUEUE_POLICY_NAME:-ForgeQueueProducerInline}"
BUCKET="${FORGE_QUEUE_BUCKET:-forge-queue-${ACCOUNT}-${REGION}}"
ARTIFACT_PREFIX="${EVENTFORGE_ARTIFACT_PREFIX:-event-forge}"

POLICY=$(cat <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "ForgeQueueSqsProducer",
      "Effect": "Allow",
      "Action": ["sqs:SendMessage", "sqs:GetQueueUrl"],
      "Resource": "arn:aws:sqs:${REGION}:${ACCOUNT}:fq-*"
    },
    {
      "Sid": "ForgeQueueS3Objects",
      "Effect": "Allow",
      "Action": ["s3:PutObject", "s3:GetObject", "s3:DeleteObject"],
      "Resource": "arn:aws:s3:::${BUCKET}/*"
    },
    {
      "Sid": "ForgeQueueS3List",
      "Effect": "Allow",
      "Action": ["s3:ListBucket"],
      "Resource": "arn:aws:s3:::${BUCKET}",
      "Condition": {
        "StringLike": {
          "s3:prefix": ["${ARTIFACT_PREFIX}/*", "${ARTIFACT_PREFIX}"]
        }
      }
    }
  ]
}
EOF
)

echo "▶ Updating inline policy ${POLICY_NAME} on role ${ROLE_NAME} (bucket=${BUCKET})"
aws iam put-role-policy \
  --role-name "$ROLE_NAME" \
  --policy-name "$POLICY_NAME" \
  --policy-document "$POLICY" \
  --region "$REGION"

echo "▶ IAM simulate (DeleteObject + ListBucket)"
aws iam simulate-principal-policy \
  --policy-source-arn "arn:aws:iam::${ACCOUNT}:role/${ROLE_NAME}" \
  --action-names s3:DeleteObject \
  --resource-arns "arn:aws:s3:::${BUCKET}/${ARTIFACT_PREFIX}/jobs/probe/object.bin" \
  --query 'EvaluationResults[0].EvalDecision' --output text
aws iam simulate-principal-policy \
  --policy-source-arn "arn:aws:iam::${ACCOUNT}:role/${ROLE_NAME}" \
  --action-names s3:ListBucket \
  --resource-arns "arn:aws:s3:::${BUCKET}" \
  --context-entries "ContextKeyName=s3:prefix,ContextKeyValues=${ARTIFACT_PREFIX}/jobs/probe/,ContextKeyType=string" \
  --query 'EvaluationResults[0].EvalDecision' --output text

echo "✔ Done. No ECS restart required for IAM; next purge with delete_s3:true should succeed."
