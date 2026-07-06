# EventForge

Standalone GPU fleet platform — HTTP job queue + WebSocket event bus + ops dashboard.

**Repository root** — this folder is the product. LoboForge (separate monorepo) is a consumer only.

**For AI/coding agents:** read [`AGENTS.md`](./AGENTS.md).

## Quick start

```bash
dotnet test EventForge.Tests/EventForge.Tests.csproj -c Release
cd web && npm ci && npm run build && cd ..
dotnet run
# http://localhost:8090/  landing  |  http://localhost:8090/ops  dashboard
```

Deploy: `docs/deploy-to-prod.md`


## Architecture: EventForge owns GPU

EventForge is the **sole GPU fleet provider** (check-in, claim, Vast provisioning, ops UI). **LoboForge is consumer-only**: it enqueues jobs via the app API key, listens for completion events, and surfaces queue state — it does not register workers or rent boxes.

Worker bootstrap scripts and `/agent/*` provisioning endpoints live here, not on LoboForge.

## Vast fleet patch (EventForge check-in)

After changing `loboforge_agent_eventforge.py`, repatch running boxes (code deploy alone is not enough):

```bash
bash scripts/patch-vast-fleet-eventforge.sh
# or one box: INSTANCE_IDS="43664556" bash scripts/patch-vast-fleet-eventforge.sh
```

New Vast rents curl bootstrap scripts from **EventForge first** (`GET /agent/provision_worker.sh` on `PublicUrl`), with LoboForge `AgentScriptBaseUrl` as fallback.


## v2 flow

```text
App  → POST /v1/jobs
Worker → POST /v1/jobs/claim
Worker → POST /v1/workers/check-in   (health + models, every minute)
Worker → PUT /v1/jobs/{id}/output   (streaming image upload)
Worker → POST /v1/jobs/{id}/stream  (roleplay token chunks)
Worker → POST /v1/jobs/{id}/complete
Consumer → WSS /v1/ws subscribe + replay
Ops      → GET /v1/ops/* + WSS /v1/ops/ws (ops API key)
```

## Run

```bash
cd event-forge
dotnet run
```

Build ops dashboard (served from `wwwroot/`):

```bash
cd event-forge/web && npm ci && npm run build
```

Health: `GET /health`, `GET /healthws`

## Ops dashboard

- **URL:** `https://eventforge.loboforge.com/ops` (public landing at `/`)
- **Auth:** `X-EventForge-Ops-Key` header or `Authorization: Bearer <ops-key>` (also `?token=` for WebSocket)
- **Config:** `EventForge:OpsKey` in secrets / `APP_SECRETS_JSON`
- **Views:** fleet, queue depth, active jobs, failures, Vast.ai rent/terminate
- **Live updates:** `WSS /v1/ops/ws` + 15s snapshot polling fallback

## Worker check-in

```bash
curl -sS -X POST "$EVENT_FORGE_URL/v1/workers/check-in" \
  -H "Authorization: Bearer $EVENT_FORGE_WORKER_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"node_uuid":"…","hostname":"…","transport":"eventforge","busy":false,"models":{}}'
```

GPU agents (`loboforge_agent_eventforge.py`, `loboforge_ollama_agent_eventforge.py`) check in to EventForge only. LoboForge is a consumer (enqueue + listen for job events).

## WebSocket events (consumers)

| Type | Purpose |
|------|---------|
| `forge.job.started` | Job claimed / processing |
| `forge.job.completed` | Image / final job manifest |
| `forge.job.failed` | Failure |
| `forge.stream.token` | Roleplay/text delta |
| `forge.stream.done` | Stream finished (full text) |

## Vast.ai provisioning

Ops API under `/v1/ops/vast/*` (same ops key). Rents inject `EVENT_FORGE_URL`, `EVENT_FORGE_WORKER_KEY`, and `LOBO_GEN_QUEUE=eventforge` into onstart/extra_env.

Configure `EventForge:VastAi:ApiKey`, `EventForge:WorkerSecret`, `EventForge:HuggingFaceToken`, `EventForge:AgentScriptBaseUrl` (LoboForge `/agent/*` scripts).

## Local worker (wrath)

```bash
python3 event_forge_worker.py --capability ollama-chat --once
```

## Storage

- Jobs + events: in-memory with SQLite write-behind (`event-forge.db`)
- Artifacts: local `artifacts/` by default; enable `EventForge:Artifacts:S3` for prod

## Production

- **Single ECS task only** — in-memory queue is not shared across tasks.
- Docker: `event-forge/Dockerfile` — builds ops SPA + .NET, pushed by root `buildspec.yml`.
- Upload saturation returns **503** + `Retry-After: 15` when `MaxConcurrentUploads` slots are full.
- Deploy: push `main` (CodeBuild) per `docs/deploy-to-prod.md`; EventForge ECS service `eventforge`.
