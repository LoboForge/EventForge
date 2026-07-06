# EventForge — GPU fleet as a service

EventForge is the **GPU provider** for the LoboForge ecosystem and white-labeled integrators. Apps enqueue work; EventForge runs workers, storage, and fleet operations.

## Product summary

- **Model:** Production GPU fleet as a service — pay per job
- **No** customer-provisioned boxes, fleet management, or manual scaling
- **Yes** HTTP queue, WebSocket event bus, artifact handling, multi-tenant API keys

Public marketing site: https://eventforge.loboforge.com/

## Integrator flow

1. Obtain an app API key (maps to `app_id`).
2. `POST /v1/jobs` with `capability`, optional `tier`, and opaque `payload`.
3. Connect to `WSS /v1/ws?token=API_KEY`, send `hello`, `subscribe`, and `replay` after downtime.
4. Handle `forge.job.completed` / `failed` / etc.; filter by `manifest.tenant_id` on shared fleets.
5. Fetch artifacts from keys/URLs in the completion manifest (uploaded via EventForge during worker `PUT /output`).

## Worker flow (EventForge-managed)

Workers use a **worker key**, not the app key:

1. `POST /v1/workers/check-in` — health, GPU, capabilities
2. `POST /v1/jobs/claim` — server picks highest-priority matching job
3. Execute locally, `PUT /v1/jobs/{id}/output`, then `POST …/complete` or `/fail`

Bootstrap and Vast provisioning scripts are served from EventForge (`/agent/*`).

## Health (no auth)

- `GET /health`
- `GET /healthws`

## Ops console

Authenticated operators use https://eventforge.loboforge.com/ops (not indexed). Fleet, queue, failures, Vast.ai rent/terminate.

## Full API reference

See `docs/QueueIntegration.md` in the LoboForge.Studio repository for exhaustive endpoint documentation, error codes, tier priority, and LoboForge reference implementations.

## Operator / coding agent runbook

See `/ai-context/agents.md` (same content as `event-forge/AGENTS.md` in the repo) for architecture, auth, provisioning, fleet patch commands, and troubleshooting — without reading the entire codebase.

## Contact

Integration access: ops@loboforge.com
