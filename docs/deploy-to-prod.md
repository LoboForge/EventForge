# Deploy EventForge to production

Standalone repo: push `main` → CodeBuild → ECR `eventforge` → ECS service `eventforge` (cluster `loboforge`, us-east-2).

## Pre-flight

```bash
dotnet test EventForge.Tests/EventForge.Tests.csproj -c Release
cd web && npm ci && npm run build && cd ..
```

Copy `secrets.example.json` → `secrets.local.json` for local fleet patch scripts (gitignored).

## Deploy

1. Push to `main` on this repository (wire CodeBuild project to this repo — see `buildspec.yml`).
2. Or local image push + ECS roll:

```bash
docker build -t eventforge:local .
# tag + push to 994185520581.dkr.ecr.us-east-2.amazonaws.com/eventforge:latest
aws ecs update-service --cluster loboforge --service eventforge --force-new-deployment --region us-east-2
```

## Verify

```bash
curl -sf https://eventforge.loboforge.com/health
curl -sf https://eventforge.loboforge.com/agent/provision_worker.sh | head -1
```

## One-time infra

| Script | Purpose |
|--------|---------|
| `scripts/setup-eventforge-tunnel.sh` | Cloudflare tunnel + sidecar |
| `scripts/bootstrap-eventforge-ecs.sh` | ECS task/service (desiredCount=1) |
| `scripts/setup-eventforge-cloudmap.sh` | In-VPC DNS (optional) |

DNS runbook: `docs/runbooks/eventforge-dns.md`

## Fleet patch (after agent/bootstrap changes)

```bash
bash scripts/patch-vast-fleet-eventforge.sh
```

Requires `secrets.local.json` with `EventForge.VastAi.ApiKey` and worker key, or env vars.

## Critical constraint

**Single ECS task only** — in-memory job queue. Do not scale `desiredCount` beyond 1 until queue state is externalized.

## LoboForge consumer

LoboForge (separate repo) enqueues via `POST /v1/jobs` and listens on `WSS /v1/ws`. See `docs/QueueIntegration.md`.
