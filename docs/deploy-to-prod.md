# Deploy EventForge to production

**Standalone repo only.** Push `main` → GitHub Actions → CodeBuild `eventforge_github` → ECR → ECS `eventforge`.

| Item | Value |
|------|--------|
| ECR image | `994185520581.dkr.ecr.us-east-2.amazonaws.com/loboforge:eventforge-standalone-<commit>` |
| Mutable pointer | `loboforge:eventforge-standalone-latest` |
| ECS cluster / service | `loboforge` / `eventforge` |
| Region | `us-east-2` |
| Public URL | https://eventforge.loboforge.com |

**Do not use** `loboforge:eventforge-latest` — the LoboForge.Studio monorepo still builds that tag from stale embedded `event-forge/` code. ECS task definitions must reference `eventforge-standalone-*` only.

## Pre-flight

```bash
dotnet test EventForge.Tests/EventForge.Tests.csproj -c Release
cd web && npm ci && npm run build && cd ..
```

Copy `secrets.example.json` → `secrets.local.json` for local fleet patch scripts (gitignored).

## Deploy (automatic)

1. Push to `main` on **this** repository.
2. GitHub Actions workflow `.github/workflows/deploy.yml` starts CodeBuild and waits for success.
3. CodeBuild runs tests, builds Docker, pushes `eventforge-standalone-<sha>`, registers a new ECS task definition, and rolls the service.
4. Health gate: `scripts/eventforge-deploy-health-gate.sh`

Monitor: GitHub Actions on LoboForge/EventForge · CloudWatch `/aws/codebuild/eventforge_github`

## Deploy (manual)

```bash
aws codebuild start-build \
  --project-name eventforge_github \
  --source-version main \
  --region us-east-2
```

Or after a local image build (requires Docker + ECR login):

```bash
COMMIT=$(git rev-parse --short HEAD)
IMAGE=994185520581.dkr.ecr.us-east-2.amazonaws.com/loboforge:eventforge-standalone-$COMMIT
docker build -t "$IMAGE" .
docker push "$IMAGE"
bash scripts/eventforge-ecs-deploy.sh "$IMAGE"
bash scripts/eventforge-deploy-health-gate.sh https://eventforge.loboforge.com "$COMMIT"
```

## Verify

```bash
curl -sf https://eventforge.loboforge.com/health
curl -sf https://eventforge.loboforge.com/agent/provision_worker.sh | head -1
bash scripts/eventforge-deploy-health-gate.sh
```

Check ECS is on a standalone image:

```bash
aws ecs describe-services --cluster loboforge --services eventforge --region us-east-2 \
  --query 'services[0].taskDefinition' --output text | xargs -I{} \
  aws ecs describe-task-definition --task-definition {} --region us-east-2 \
  --query 'taskDefinition.containerDefinitions[?name==`eventforge`].image' --output text
# Must contain eventforge-standalone-
```

## One-time infra

| Script | Purpose |
|--------|---------|
| `scripts/setup-eventforge-tunnel.sh` | Cloudflare tunnel + sidecar |
| `scripts/bootstrap-eventforge-ecs.sh` | ECS task/service (desiredCount=1) |
| `scripts/setup-eventforge-cloudmap.sh` | In-VPC DNS (optional) |
| `scripts/eventforge-ecs-deploy.sh` | Register task def + roll service |
| `scripts/eventforge-deploy-health-gate.sh` | Post-deploy smoke checks |

DNS runbook: `docs/runbooks/eventforge-dns.md`

## Fleet patch (after agent/bootstrap changes)

```bash
bash scripts/patch-vast-fleet-eventforge.sh
```

Requires `secrets.local.json` with `EventForge.VastAi.ApiKey` and worker key, or env vars.

## Critical constraints

1. **Single ECS task only** — in-memory job queue. `desiredCount` must stay `1`.
2. **Production — never scale to zero.** `desiredCount=0` takes https://eventforge.loboforge.com fully offline (real customer jobs). To restart, use `--desired-count 1 --force-new-deployment` (see `AGENTS.md`).
3. **No overlapping tasks during deploy** — service uses `minimumHealthyPercent=0`, `maximumPercent=100`, AZ rebalancing disabled.
4. **Circuit breaker** — failed deploys roll back automatically.
5. **Monorepo must not deploy EventForge** — LoboForge.Studio CI should skip ECS roll for `eventforge` service.
6. **Pre-deploy persist** — CodeBuild/`eventforge-ecs-deploy.sh` calls `scripts/eventforge-pre-deploy-persist.sh`, which hits `POST /v1/ops/jobs/flush-backup`. That path hot-copies SQLite under `/tmp` then uploads to S3. If it reports `backup_skipped` with thousands of jobs, **do not** roll ECS until backup succeeds (or you have verified a fresh S3 `event-forge/store.db`). Never set `desiredCount=0` to work around a bad backup.

## Emergency restart (prod)

```bash
# Replace running task — keeps desiredCount=1
aws ecs update-service --cluster loboforge --service eventforge \
  --desired-count 1 --force-new-deployment --region us-east-2

# If someone set desiredCount=0, recover immediately:
aws ecs update-service --cluster loboforge --service eventforge \
  --desired-count 1 --region us-east-2

curl -sf https://eventforge.loboforge.com/health
```

## LoboForge consumer

LoboForge enqueues via `POST /v1/jobs` and listens on `WSS /v1/ws`. See `docs/QueueIntegration.md`.

