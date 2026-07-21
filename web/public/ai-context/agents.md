# EventForge — guide for AI agents

This page is written for autonomous AI agents (and the developers who run them) that want to **request and use GPU inference capacity** on EventForge programmatically.

Canonical site: https://eventforge.loboforge.com/
Machine-readable summary: https://eventforge.loboforge.com/llms.txt
Full integration context: https://eventforge.loboforge.com/ai-context/eventforge-context.md
Enterprise contact: sales@loboforge.com

## What EventForge sells

Production GPU inference capacity, activated after an ops-reviewed request and offline payment:

- One job queue API for image, video, music, and text generation models
- Priority tiers: `admin`, `vip`, `normal`, `bulk`
- **Custom LoRA support**: upload your own fine-tune weights; jobs route only to workers that have them
- Results over WebSocket (`WSS /v1/ws`) with replay, or HTTP polling (`GET /api/v1/events?since=…`)
- Artifact storage included
- Capacity that scales with your workload — you never provision or manage GPUs

## Discover plans and models (no auth)

- `GET /v1/public/plans` → `{ plans: [{ id, name, description, price_usd, credits, features }], enterprise_contact }`
  - Starter $29 / 1,000 credits · Pro $99 / 4,000 credits · Scale $299 / 14,000 credits · Enterprise: sales@loboforge.com
- `GET /v1/public/models` → `{ models: [{ id, name, kind, description, supports_custom_loras }] }`
  - Includes Wan 2.2 14B (text-to-video and image-to-video), FLUX.2 Klein and Klein Edit, Z-Image Turbo, Chroma HD, LTX-2 Video, ACE-Step (music), JoyCaption (captioning), Dolphin LLM (text). All image and video models support custom LoRAs.

## Request capacity (agent-executable)

```text
1. GET /v1/public/models
   → choose one or more model ids

2. POST /v1/public/capacity-request
   { "email", "company"?, "name"?, "models": [...], "estimated_jobs": 1000,
     "notes"?, "preferred_payment": "paypal"|"wire"|"monero"|"any" }
   → { request_id, status: "received", message }

3. Ops reviews the request and manually sends a PayPal invoice, wire details,
   or Monero address plus unique order reference.

4. After payment clears, ops activates an efk_ API key and sends it securely.
```

## Run jobs (API key auth)

Switch from the session token to the account `api_key`: `Authorization: Bearer <api_key>`.

- Enqueue: `POST /v1/jobs` with `capability` (e.g. `image`, `video`, `ltx`, `ollama`), optional `tier`, and a `payload` containing the model id, prompt, and optional LoRA references.
- Upload custom LoRAs first (optional): `POST /v1/assets/loras` (multipart), then reference by filename with a `strength` value in job payloads.
- Listen: connect `WSS /v1/ws?token=API_KEY`, subscribe, and handle `forge.job.started` / `forge.job.completed` / `forge.job.failed` / `forge.job.timeout` / `forge.job.released`, plus `forge.stream.token` / `forge.stream.done` for streaming text.
- Replay after downtime: `GET /api/v1/events?since=…`
- Fetch artifacts from URLs in the completion manifest.

## Health (no auth)

- `GET /health` — service liveness
- `GET /healthws` — WebSocket liveness

## Notes for agents

- Starting packages are estimates. Submit another capacity request when more volume is needed.
- Keep the `api_key` secret; it authorizes spending against your credit balance.
- The ops console at `/ops` is for EventForge operators only, not customers.
- For contractual SLAs, dedicated capacity, or invoicing, email sales@loboforge.com.
