# EventForge — GPU inference capacity as a service

EventForge provides production GPU inference capacity. Developers and autonomous AI agents submit the models and approximate job volume they need; ops reviews the request, arranges offline payment, and activates an API key after funds clear.

Public site: https://eventforge.loboforge.com/
Capacity request: https://eventforge.loboforge.com/request
Account / API key: https://eventforge.loboforge.com/login
Machine-readable summary: https://eventforge.loboforge.com/llms.txt
Enterprise: sales@loboforge.com

## Product summary

- **What you request:** model-specific production capacity and an approximate job volume. Starting prepaid packages provide estimate guidance.
- **What you get:** an API key, one job queue API for every model, priority tiers (admin / vip / normal / bulk), custom LoRA support, artifact storage, and results over WebSocket or HTTP polling.
- **Elastic capacity:** capacity scales with your workload. You never provision boxes, manage fleets, or write scaling policies.

## Model catalog

Query live: `GET /v1/public/models` (no auth) → `{ models: [{ id, name, kind, description, supports_custom_loras }] }`

| Model | Kind | Custom LoRAs |
|-------|------|--------------|
| Wan 2.2 14B Text-to-Video | video | yes |
| Wan 2.2 14B Image-to-Video | video | yes |
| FLUX.2 Klein | image generation | yes |
| FLUX.2 Klein Edit | image editing | yes |
| Z-Image Turbo | image generation | yes |
| Chroma HD | image generation | yes |
| LTX-2 Video | video | yes |
| ACE-Step | music generation | no |
| JoyCaption | image captioning | no |
| Dolphin LLM | text generation | no |

## Custom LoRAs (headline feature)

Bring your own fine-tunes. Upload LoRA weights once via `POST /v1/assets/loras` (API key auth, multipart), then reference them by filename in any image or video job payload. EventForge routes each job **only to workers that have the required LoRAs available**, so results are consistent. LoRAs are private to your account. Multiple LoRAs can be stacked with per-LoRA strength.

## Starting packages and payment

Query live: `GET /v1/public/plans` (no auth) → `{ plans: [{ id, name, description, price_usd, credits, features }], enterprise_contact }`

| Plan | Price | Credits |
|------|-------|---------|
| Starter | $29 | 1,000 |
| Pro | $99 | 4,000 |
| Scale | $299 | 14,000 |
| Enterprise | custom (SLAs, dedicated capacity, invoicing) | contact sales@loboforge.com |

Starting packages are price anchors, not an automated checkout. Ops settles approved requests by PayPal invoice, wire transfer, or Monero. Access is activated manually after payment clears.

## Programmatic capacity request

All endpoints are JSON over HTTPS at https://eventforge.loboforge.com.

1. Discover models with `GET /v1/public/models` and optional starting packages with `GET /v1/public/plans`.
2. Submit `POST /v1/public/capacity-request` with `{ "email", "company"?, "name"?, "models": string[], "estimated_jobs": number, "notes"?, "preferred_payment": "paypal"|"wire"|"monero"|"any" }`.
3. Store the returned `request_id`. Ops reviews the request and sends payment instructions manually.
4. After payment clears, ops creates or reuses the account and activates an `efk_` API key. Existing registered users can retrieve it with `GET /v1/public/account`; otherwise ops sends it through a secure channel.

Automated `POST /v1/public/checkout`, capture, and NOWPayments webhook routes are retired and return HTTP 410 with `manual_billing`.

## Submitting jobs (API key auth)

Use the account API key, not the session token: `Authorization: Bearer <api_key>`.

1. `POST /v1/jobs` with `capability` (e.g. `image`, `video`, `ltx`, `ollama`), optional `tier` (`admin` / `vip` / `normal` / `bulk`), and an opaque `payload` (model id, prompt, LoRA references, etc.).
2. Connect to `WSS /v1/ws?token=API_KEY`, send `hello`, `subscribe`, and `replay` after downtime.
3. Handle lifecycle events: `forge.job.started`, `forge.job.completed`, `forge.job.failed`, `forge.job.timeout`, `forge.job.released`, and streaming events `forge.stream.token` / `forge.stream.done` for text models.
4. Alternatively poll `GET /api/v1/events?since=…` over plain HTTP.
5. Fetch artifacts from the URLs in the completion manifest (stored by EventForge).

Example:

```bash
curl -X POST https://eventforge.loboforge.com/v1/jobs \
  -H "Authorization: Bearer $EVENTFORGE_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "capability": "image",
    "tier": "normal",
    "payload": {
      "model": "flux-2-klein",
      "prompt": "product photo of a titanium watch, studio lighting",
      "loras": [{ "name": "my-brand-style.safetensors", "strength": 0.8 }]
    }
  }'
```

## Health (no auth)

- `GET /health`
- `GET /healthws`

## Ops console

Authenticated operators use https://eventforge.loboforge.com/ops (not indexed). Not for customers.

## Full API reference

See `docs/QueueIntegration.md` in the LoboForge.Studio repository for exhaustive endpoint documentation, error codes, tier priority, and reference implementations.

## Operator / coding agent runbook

See `/ai-context/agents.md` (same content as `AGENTS.md` in the repo) for architecture, auth, provisioning, and troubleshooting.

## Contact

- Enterprise, SLAs, dedicated capacity: sales@loboforge.com
- Operations / integration support: ops@loboforge.com
