# EventForge Cloudflare token capabilities (2026-07-05)

Incident: f040a3f2 removed `eventforge.loboforge.com` from loboforge-aws without a working DNS CNAME to eventforge-aws.

| Source | DNS list/upsert (`Zone.DNS Edit`) | Tunnel config (`Account.Cloudflare Tunnel`) |
|--------|-------------------------------------|---------------------------------------------|
| `loboforge/cloudflare-api-token` (us-east-2, rolled 2026-07-05) | **No** — code 10000 (has Account DNS Settings, not **Zone → DNS → Edit**) | **Yes** — verify active; GET/PUT tunnels |

**Active routing (2026-07-05):** loboforge-aws ingress `eventforge.loboforge.com` → ECS task public IP `:8090` (currently `3.138.137.54`).

**Do not** remove loboforge-aws eventforge ingress until DNS CNAME to `6541dff9-f29a-4a48-b3d7-c3a489496293.cfargotunnel.com` is created with a DNS-capable token stored in Secrets Manager.

**Action for user:** Roll a new Cloudflare API token with **Zone → DNS → Edit** for `loboforge.com` and update `loboforge/cloudflare-api-token`; keep tunnel permissions on the same token or a separate tunnel-only token.
