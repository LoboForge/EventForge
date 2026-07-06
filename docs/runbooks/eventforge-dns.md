# EventForge DNS — match www.loboforge.com

## Correct architecture (same pattern as main site)

| Hostname | ECS service | Cloudflare tunnel | Sidecar origin | DNS target |
|----------|-------------|-------------------|----------------|------------|
| `www.loboforge.com` | loboforge | **loboforge-aws** `7d16bdae-…` | `http://127.0.0.1:8080` | CNAME → `7d16bdae-3019-4e9e-a4ce-8510a9688e8f.cfargotunnel.com` **or** Tunnel row → loboforge-aws |
| `eventforge.loboforge.com` | eventforge | **eventforge-aws** `6541dff9-…` | `http://127.0.0.1:8090` | CNAME → `6541dff9-f29a-4a48-b3d7-c3a489496293.cfargotunnel.com` **or** Tunnel row → eventforge-aws |

**Wrong (causes outages):**

- `eventforge` on **loboforge-aws** tunnel (IP bridge or sidecar proxy)
- Both **Tunnel** and **CNAME** rows for `eventforge` (Cloudflare may publish neither → NXDOMAIN)
- CNAME to **loboforge-aws** tunnel ID
- Emergency task public IP in secrets

## One-time DNS (dashboard)

Cloudflare → **loboforge.com** → **DNS**:

1. Delete **all** existing rows for `eventforge` (Tunnel, CNAME, anything).
2. Add **one** record:

**Option A — CNAME (matches `scripts/cutover-cloudflare-dns.sh` for www):**

| Field | Value |
|-------|--------|
| Type | CNAME |
| Name | `eventforge` |
| Target | `6541dff9-f29a-4a48-b3d7-c3a489496293.cfargotunnel.com` |
| Proxy | Proxied ON |

**Option B — Tunnel row (matches dashboard UI for www/loboforge.com):**

| Field | Value |
|-------|--------|
| Type | Tunnel |
| Name | `eventforge` |
| Target | `eventforge-aws` |
| Proxy | Proxied ON |

3. Save. Wait ~1 min. `curl -sf https://eventforge.loboforge.com/health`

## Fix tunnel routing (API — no Zone DNS Edit needed)

```bash
./scripts/fix-eventforge-routing.sh
```

## Full setup (tunnel + DNS when token has Zone DNS Edit)

```bash
./scripts/setup-eventforge-tunnel.sh --merge-secrets
```
