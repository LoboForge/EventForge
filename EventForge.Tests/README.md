# EventForge.Tests

xUnit + FluentAssertions integration/unit tests for the EventForge provider.

## Layout

| Folder | Purpose |
|--------|---------|
| `Api/` | HTTP auth helpers and route behavior |
| `Configuration/` | Secrets/options binding |
| `Hosting/` | Startup timing, `/health`, non-blocking initialization |
| `Infrastructure/` | Worker claim policy, bootstrap defaults |
| `Queue/` | In-memory queue ordering, leases, claim gates |
| `Services/` | Fleet tracker and job lifecycle helpers |
| `Support/` | Test doubles shared across suites |

## Run locally

```bash
# Backend only
dotnet test EventForge.Tests/EventForge.Tests.csproj

# Full gate (backend + web build) — same as CI
bash scripts/ci.sh
```

## CI

- **GitHub Actions:** `.github/workflows/ci.yml` on every push/PR to `main`
- **CodeBuild deploy:** `buildspec.yml` runs `dotnet test` before Docker build

## Startup contract

`StartupInitializationService` must not block Kestrel from accepting requests while SQLite/S3 restore runs. Tests in `Hosting/` enforce:

1. `StartAsync` returns immediately
2. `GET /health` responds within a few seconds even when restore is artificially slow
3. `cache_loaded` becomes `true` after background init completes

When adding persistence or hosted startup work, extend `Hosting/` tests if behavior affects cold start.
