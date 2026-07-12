# EventForge — agent instructions

Read this before changing EventForge code, ops workflows, or GPU fleet boxes. You should **not** need to scan the whole monorepo to understand how the product works.

**Web copy (LLM crawlers):** keep `web/public/ai-context/agents.md` in sync when you edit this file (served at `/ai-context/agents.md`).

**Public site:** https://eventforge.loboforge.com/  
**Ops console:** https://eventforge.loboforge.com/ops (requires ops key; `noindex`)  
**Integrator API docs:** `docs/QueueIntegration.md`  
**Worker provisioning (Vast rent/patch):** `docs/worker-provisioning.md`  
**Deploy:** `docs/deploy-to-prod.md`

---

## Production safety (mandatory)

**https://eventforge.loboforge.com is live production** — LoboForge and other integrators enqueue real customer jobs here. Treat every ECS change as customer-facing.

| Do | Don't |
|----|-------|
| Keep `desiredCount=1` always | **Never** set `desiredCount=0` to "restart" — that takes the site fully offline (Cloudflare 530) |
| Restart with `--desired-count 1 --force-new-deployment` | Stop/delete the service or leave it at 0 without immediately scaling back |
| Verify `/health` returns 200 after any ECS change | Assume a deploy "completed" if `/health` is down |
| **Keep old workers until replacements check in and claim jobs** | Terminate the only box handling a capability before new boxes are provisioned |

**Fleet cutover rule:** never destroy/stop a Vast worker that is the **only** box polling a capability (`wan`, `ltx`, `image`, …) until at least one **replacement** has finished provisioning, checked into `/v1/ops/fleet`, and successfully claimed a job (or you've confirmed two+ healthy replacements). Rent first, verify, then retire.

**Correct prod restart** (replaces the running task; ~30–90s gap, then self-recovers):

```bash
aws ecs update-service --cluster loboforge --service eventforge \
  --desired-count 1 --force-new-deployment --region us-east-2
```

If `desiredCount` is already 0, scale back immediately — do not wait for a deploy:

```bash
aws ecs update-service --cluster loboforge --service eventforge \
  --desired-count 1 --region us-east-2
```

---

## Product model (locked)

| Role | Service | Responsibility |
|------|---------|----------------|
| **Provider** | **EventForge** | GPU fleet, worker check-in, job queue, claim/lease, artifact storage, WebSocket event bus, Vast rent/provision, bootstrap scripts at `/agent/*`, ops dashboard |
| **Consumer** | **LoboForge** (and white-labeled apps) | Enqueue jobs (`POST /v1/jobs`), subscribe to events (`WSS /v1/ws`), show queue state — **no** worker registration, **no** Vast admin, **no** fleet ownership |

LoboForge paths that used to own GPU (`POST /api/agent/check-in`, `api/admin/vastai/*`, local fleet registry for dispatch) are **deprecated (410)**. Workers and ops tooling must target EventForge.

---

## Architecture (30-second version)

```text
App (API key)  → POST /v1/jobs
Worker (worker key) → POST /v1/workers/check-in (every ~60s)
                    → POST /v1/jobs/claim
                    → PUT  /v1/jobs/{id}/output
                    → POST /v1/jobs/{id}/complete | /fail | /release
App            → WSS /v1/ws (subscribe + replay) and/or GET /api/v1/events?since=
Ops (ops key)  → GET /v1/ops/*, WSS /v1/ops/ws, Vast rent/terminate
```

- **Queue:** in-memory per ECS task — production runs **exactly one** EventForge task (`desiredCount=1`).
- **Events:** worker `POST /complete` (or `/fail`) → EventForge persists (SQLite + optional S3 backup) → WebSocket fanout to app subscribers. **No SQS** in the active path.
- **Workers:** Python agents on Vast boxes; bootstrap scripts fetched from EventForge first, LoboForge `/agent/*` as fallback.

**Legacy (remove, do not use):** `SqsIngressConsumer`, `IngressQueueUrl`, `forge-queue/` CDK, and `GenSqsJobQueue` SQS fallback paths are stale cutover code. Prod uses `Fleet:GenQueue:Mode=eventforge` only.

---

## Auth (three keys — do not confuse)

| Key | Header | Used for |
|-----|--------|----------|
| **App API key** | `Authorization: Bearer …` | `POST /v1/jobs`, `WSS /v1/ws`, `GET /api/v1/events`, `GET /v1/queue/stats` |
| **Worker key** | `Authorization: Bearer …` | `POST /v1/workers/check-in`, claim, output, complete, fail, release |
| **Ops key** | `X-EventForge-Ops-Key` or `Authorization: Bearer …` | `/v1/ops/*`, `/v1/ops/ws`, ops UI |

Config: `EventForge:*` in `appsettings.json` / `APP_SECRETS_JSON` (`ApiKey`, `WorkerKey`, `OpsKey`, `PublicUrl`, `VastAi:ApiKey`, …).

**Public (no auth):** `GET /health`, `GET /healthws`, `GET /agent/{script}`, marketing `/`, `/llms.txt`, `/ai-context/*`.

---

## Repo layout (where to look)

| Path | What |
|------|------|
| `event-forge/Program.cs` | Host, middleware, static wwwroot, WS endpoints |
| `event-forge/Api/` | HTTP routes (jobs, workers, ops, vast, agent scripts) |
| `event-forge/Services/` | Queue, fleet tracker, jobs (ignore `SqsIngressConsumer.cs` — legacy) |
| `event-forge/WebSocket/` | Consumer WS protocol + ops hub |
| `event-forge/VastAi/` | Vast client, rent/terminate, disk modes |
| `event-forge/Infrastructure/WorkerBootstrapDefaults.cs` | Bootstrap URL order, on-start env for Vast |
| `event-forge/web/` | React: public landing `/`, ops `/ops` |
| `event-forge.tests/` | Unit tests |
| `loboforge_agent_eventforge.py` | Production GPU agent (Comfy/image/video) |
| `loboforge_ollama_agent_eventforge.py` | Ollama / text worker agent |
| `provision_worker.sh`, `provision_ltx_native.sh`, … | Bootstrap (copied into Docker image + served at `/agent/*`) |
| `scripts/` | Deploy, tunnel, ECS bootstrap, Vast fleet patch |
| `agent/` | Worker bootstrap scripts + Python agents (served at `/agent/*` in Docker) |
| `vendor/loboforge_worker/` | Minimal worker helpers for fleet patch SSH |
| `docs/QueueIntegration.md` | Full integrator reference |
| `docs/worker-provisioning.md` | Vast rent/search, patch scripts, credentials, common failures |

---

## Deploy EventForge

EventForge ships from **this repository** via `buildspec.yml` → ECR `eventforge` → ECS `eventforge`.

Before prod changes, read **`docs/deploy-to-prod.md`**.

**Local dev:**

```bash
cd web && npm ci && npm run build
dotnet run
```

**After changing worker agents or bootstrap scripts:** code deploy alone is **not** enough for boxes already running — patch the fleet (below).

---

## Worker provisioning (how boxes boot)

1. **Vast on-start** (or manual SSH) sets env:
   - `EVENT_FORGE_URL` — usually `https://eventforge.loboforge.com`
   - `EVENT_FORGE_WORKER_KEY`
   - `LOBO_GEN_QUEUE=eventforge`
   - `FORGE_QUEUE_CAPABILITY=…` (comma list by mode: image, video, ltx, ollama, …)
2. Box curls bootstrap from **EventForge first**:
   - `https://eventforge.loboforge.com/agent/provision_worker.sh` (or `provision_ltx_native.sh`, etc.)
   - Fallback: `AgentScriptBaseUrl` (LoboForge) `/agent/…`
3. Bootstrap installs deps, pulls `loboforge_agent_eventforge.py` (+ common), starts systemd/tmux agent.
4. Agent loop: **check-in** → **claim** → run job → **upload output** → **complete**.

Scripts are allowlisted in `Api/AgentEndpoints.cs` and baked into the Docker image from repo root (`Dockerfile` COPY lines).

**New rents** from ops UI (`/ops` → Vast tab) inject EventForge env via `WorkerBootstrapDefaults.ApplyEventForgeVastExtraEnv`.

---

## Fix / patch provisioned workers (runbook)

### When to patch

- Changed `loboforge_agent_eventforge.py`, `loboforge_agent_common.py`, or bootstrap `.sh` files.
- Workers show wrong transport, stale LoboForge check-in, or missing from ops fleet.
- After rotating `EVENT_FORGE_WORKER_KEY` (patch + restart required on boxes).

### Patch entire Vast fleet

```bash
# From this repo root — copy secrets.example.json → secrets.local.json (Vast + worker keys)
bash scripts/patch-vast-fleet-eventforge.sh

# One box:
INSTANCE_IDS="43664556" bash scripts/patch-vast-fleet-eventforge.sh

# Expected gen workers (default 6): 3 image, 1 video, 1 ltx, 1 ollama (+ separate ollama script)
GEN_WORKERS_EXPECT=6 bash scripts/patch-vast-fleet-eventforge.sh
```

Wrapper calls `fix-gen-eventforge-now.sh` then `patch-ollama-box-eventforge.sh`.

### Verify fleet in ops

1. Open https://eventforge.loboforge.com/ops (ops key).
2. **Fleet tab:** one row per worker; row key = `node_uuid` / hostname (not shared worker auth id).
3. **EF queue column:** should show `OK`, not `EventForge /health HTTP 403`.
4. Expected prod fleet (user baseline): **6 workers** — 3× image, 1× video, 1× LTX, 1× ollama (plus ancillary boxes like joycaption may appear if they check in).

### Diagnose a single box (SSH)

On the worker:

```bash
grep -E 'EVENT_FORGE|LOBO_GEN_QUEUE' /etc/loboforge/worker.env 2>/dev/null || true
curl -sf "${EVENT_FORGE_URL}/health"
curl -sf -H "Authorization: Bearer ${EVENT_FORGE_WORKER_KEY}" "${EVENT_FORGE_URL}/v1/fleet/workers" | head
# Agent logs — location depends on provision mode (systemd, tmux, /var/log/…)
```

Manual check-in test:

```bash
curl -sS -X POST "$EVENT_FORGE_URL/v1/workers/check-in" \
  -H "Authorization: Bearer $EVENT_FORGE_WORKER_KEY" \
  -H 'Content-Type: application/json' \
  -d '{"node_uuid":"test","hostname":"manual-test","transport":"eventforge","busy":false,"models":{}}'
```

### Common failures

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| Only 1 worker row flashing between hostnames | React row keyed by worker id, or WS sending partial fleet | Deploy latest ops UI (`56e2f8a`+); backend keys fleet by `node_uuid` |
| `EF queue` → `/health HTTP 403` | Ops auth blocking public `/health` | `/health` must stay unauthenticated |
| Workers missing entirely | Old agent, no `EVENT_FORGE_*` env, not restarted after deploy | Run `patch-vast-fleet-eventforge.sh` |
| Jobs enqueue but never complete | Workers on wrong queue / not claiming | Confirm `LOBO_GEN_QUEUE=eventforge`, agent is `loboforge_agent_eventforge.py` |
| Joycaption-only in fleet | Other boxes not checking in | Patch gen + ltx + ollama; confirm Vast instances running |
| Code pushed but UI unchanged | CodeBuild/ECS not deployed | Check CodeBuild + `eventforge` service task image tag |

---

## Consumer apps (LoboForge integrator)

- Enqueue: `apps/api/Infrastructure/Generate/EventForgeClient.cs`, `GenSqsJobQueue.cs`
- Completions: `EventForgeCompletionService.cs`, `EventForgeEventPoller.cs`
- Config: `EventForgeOptions.cs` — `EVENT_FORGE_URL`, `EVENT_FORGE_WS`, `EVENT_FORGE_API_KEY`
- **Do not** re-enable local GPU check-in or LoboForge Vast admin for new work.

Full protocol: `docs/QueueIntegration.md`.

---

## Privacy

**No cloud vision on private images** — same rule as monorepo `AGENTS.md`. Do not send user images, CDN URLs, or Comfy outputs to external vision models unless the user pasted that image in the current chat. See `.cursor/rules/no-cloud-vision-private-images.mdc`.

---

## Agent checklist (before you ship)

1. Does this change belong on **EventForge** (provider) vs **LoboForge** (consumer)?
2. If worker/agent/bootstrap changed → document fleet patch in commit/PR and plan `patch-vast-fleet-eventforge.sh`.
3. Keep `/health` public; keep ops routes behind ops key.
4. Remember single-task queue — do not scale EventForge ECS beyond 1 without a shared queue design.
5. **Never set prod `desiredCount=0`** — use `--force-new-deployment` with `--desired-count 1` to restart.
6. Run `dotnet test event-forge.tests/EventForge.Tests.csproj` for backend changes; rebuild `event-forge/web` for UI.
7. Do not commit secrets (`.env`, `appsettings.Secrets.json`, ops/worker keys).

---

## Quick links

- Landing + SEO static files: `event-forge/web/public/` (`llms.txt`, `robots.txt`, `sitemap.xml`, `ai-context/`)
- Vast ops API: `event-forge/Api/VastEndpoints.cs`
- Fleet state: `event-forge/Services/WorkerFleetTracker.cs`
- Event bus: worker HTTP complete → `JobService` → `IEventStore` → `WsConnectionManager` (see `JobEndpoints.cs`, `WsProtocolHandler.cs`)
- Design background (superseded): `docs/forge-event-bus.md` — describes old SQS ingress model; **not** prod
