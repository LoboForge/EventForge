using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EventForge.Tests.Api;

public sealed class OpsQueueModerationTests : IClassFixture<OpsModerationWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly OpsModerationWebApplicationFactory _factory;

    public OpsQueueModerationTests(OpsModerationWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static HttpRequestMessage Ops(HttpMethod method, string path, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-EventForge-Ops-Key", "dev-ops-key-change-me");
        if (json != null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private async Task EnqueueAsync(string jobId, string appKey, string prompt, string capability = "image")
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = new StringContent(
                $$"""
                {
                  "job_id": "{{jobId}}",
                  "capability": "{{capability}}",
                  "tier": "bulk",
                  "kind": "image",
                  "payload": { "type": "assign_job", "model": "flux", "prompt": "{{prompt}}" }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appKey);
        using var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task EnsureWorkerAsync(string hostname, string capability)
    {
        using var checkIn = new HttpRequestMessage(HttpMethod.Post, "/v1/workers/check-in")
        {
            Content = new StringContent(
                $$"""
                {
                  "node_uuid": "{{hostname}}",
                  "hostname": "{{hostname}}",
                  "transport": "eventforge",
                  "forge_queue_capabilities": ["{{capability}}"],
                  "claim_ready_capabilities": ["{{capability}}"],
                  "models": { "checkpoints": ["flux.safetensors"] }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
        checkIn.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
        using var checkInResp = await _client.SendAsync(checkIn);
        checkInResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task ClaimAndCompleteAsync(string hostname, string jobId, string capability, byte[]? pngBytes = null)
    {
        await EnsureWorkerAsync(hostname, capability);

        using (var claim = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs/claim")
        {
            Content = new StringContent(
                $$"""{"hostname":"{{hostname}}","capabilities":["{{capability}}"]}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var claimResp = await _client.SendAsync(claim);
            claimResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var claimDoc = JsonDocument.Parse(await claimResp.Content.ReadAsStringAsync());
            claimDoc.RootElement.GetProperty("job_id").GetString().Should().Be(jobId);
        }

        if (pngBytes != null)
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, $"/v1/jobs/{jobId}/output?file=output.png")
            {
                Content = new ByteArrayContent(pngBytes),
            };
            put.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var putResp = await _client.SendAsync(put);
            putResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var complete = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/complete")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
        using var completeResp = await _client.SendAsync(complete);
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Ops_jobs_list_requires_ops_key()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/ops/jobs?status=queued");
        using var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Ops_jobs_list_returns_prompt_for_queued_and_in_flight()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var queuedId = $"mod-q-{suffix}";
        await EnqueueAsync(queuedId, "dev-local-key", "unique_moderation_prompt_alpha");

        using (var list = Ops(HttpMethod.Get, "/v1/ops/jobs?status=queued&limit=200"))
        {
            using var resp = await _client.SendAsync(list);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var jobs = doc.RootElement.GetProperty("jobs").EnumerateArray()
                .Where(j => j.GetProperty("job_id").GetString() == queuedId)
                .ToList();
            jobs.Should().ContainSingle();
            jobs[0].GetProperty("prompt").GetString().Should().Contain("unique_moderation_prompt_alpha");
            jobs[0].GetProperty("app_id").GetString().Should().Be("fleet-lobo-remover-wrath");
            jobs[0].GetProperty("status").GetString().Should().Be("queued");
        }
    }

    [Fact]
    public async Task Cancel_by_keyword_requires_app_and_keyword_and_scopes_to_app()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var target = $"kw-target-{suffix}";
        var otherApp = $"kw-other-{suffix}";
        var miss = $"kw-miss-{suffix}";
        await EnqueueAsync(target, "dev-local-key", "banana_keyword_xyz");
        await EnqueueAsync(otherApp, "loboforge-local-key", "banana_keyword_xyz");
        await EnqueueAsync(miss, "dev-local-key", "harmless_prompt");

        using (var bad = Ops(HttpMethod.Post, "/v1/ops/jobs/cancel-by-keyword",
                   """{"app_id":"fleet-lobo-remover-wrath","keyword":""}"""))
        {
            using var resp = await _client.SendAsync(bad);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        using (var dry = Ops(HttpMethod.Post, "/v1/ops/jobs/cancel-by-keyword",
                   """{"app_id":"fleet-lobo-remover-wrath","keyword":"banana_keyword","dry_run":true,"include_in_flight":true}"""))
        {
            using var resp = await _client.SendAsync(dry);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("matched").GetInt32().Should().Be(1);
            doc.RootElement.GetProperty("cancelled").GetInt32().Should().Be(0);
            doc.RootElement.GetProperty("executed").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("job_ids_sample").EnumerateArray()
                .Select(e => e.GetString()).Should().Contain(target);
        }

        using (var run = Ops(HttpMethod.Post, "/v1/ops/jobs/cancel-by-keyword",
                   """{"app_id":"fleet-lobo-remover-wrath","keyword":"banana_keyword","dry_run":false,"include_in_flight":true,"delete_s3":true}"""))
        {
            using var resp = await _client.SendAsync(run);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("cancelled").GetInt32().Should().Be(1);
            doc.RootElement.GetProperty("executed").GetBoolean().Should().BeTrue();
        }

        // Other consumer's matching job remains; non-matching same-app job remains.
        using (var list = Ops(HttpMethod.Get, "/v1/ops/jobs?status=queued&limit=500"))
        {
            using var resp = await _client.SendAsync(list);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var ids = doc.RootElement.GetProperty("jobs").EnumerateArray()
                .Select(j => j.GetProperty("job_id").GetString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ids.Should().NotContain(target);
            ids.Should().Contain(otherApp);
            ids.Should().Contain(miss);
        }
    }

    [Fact]
    public async Task Delete_completed_job_is_idempotent_and_blocks_late_completion()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var jobId = $"del-done-{suffix}";
        var hostname = $"loboforge-image-{suffix}";
        var capability = $"mod-del-{suffix}";
        await EnqueueAsync(jobId, "dev-local-key", "delete_me_prompt", capability);

        // Minimal PNG header bytes — never decoded/analyzed; only stored as opaque artifact.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await ClaimAndCompleteAsync(hostname, jobId, capability, png);

        var artifactPath = Path.Combine(_factory.ArtifactDir, jobId, "output.png");
        File.Exists(artifactPath).Should().BeTrue();

        using (var list = Ops(HttpMethod.Get, "/v1/ops/jobs?status=completed&limit=200"))
        {
            using var resp = await _client.SendAsync(list);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var job = doc.RootElement.GetProperty("jobs").EnumerateArray()
                .First(j => j.GetProperty("job_id").GetString() == jobId);
            job.GetProperty("has_output").GetBoolean().Should().BeTrue();
            job.GetProperty("output_kind").GetString().Should().Be("image");
            job.GetProperty("output_proxy_url").GetString().Should().Contain(jobId);
            job.GetProperty("prompt").GetString().Should().Contain("delete_me_prompt");
        }

        using (var del = Ops(HttpMethod.Delete, $"/v1/ops/jobs/{jobId}"))
        {
            using var resp = await _client.SendAsync(del);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("output_artifact_deleted").GetBoolean().Should().BeTrue();
        }

        File.Exists(artifactPath).Should().BeFalse();

        // Idempotent second delete.
        using (var del2 = Ops(HttpMethod.Delete, $"/v1/ops/jobs/{jobId}"))
        {
            using var resp = await _client.SendAsync(del2);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("found").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("output_artifact_deleted").GetBoolean().Should().BeTrue();
        }

        // Output stream is gone.
        using (var outReq = Ops(HttpMethod.Get, $"/v1/ops/jobs/{jobId}/output"))
        {
            using var resp = await _client.SendAsync(outReq);
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Delete_active_job_returns_409_without_allow_active()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var jobId = $"del-active-{suffix}";
        await EnqueueAsync(jobId, "dev-local-key", "still_queued");

        using var del = Ops(HttpMethod.Delete, $"/v1/ops/jobs/{jobId}");
        using var resp = await _client.SendAsync(del);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("job_active");
    }

    [Fact]
    public async Task Cancel_in_flight_blocks_late_worker_complete()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var jobId = $"late-comp-{suffix}";
        var hostname = $"loboforge-image-late-{suffix}";
        var capability = $"mod-late-{suffix}";
        await EnqueueAsync(jobId, "dev-local-key", "late_complete_prompt", capability);
        await EnsureWorkerAsync(hostname, capability);

        using (var claim = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs/claim")
        {
            Content = new StringContent(
                $$"""{"hostname":"{{hostname}}","capabilities":["{{capability}}"]}""",
                Encoding.UTF8,
                "application/json"),
        })
        {
            claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var claimResp = await _client.SendAsync(claim);
            claimResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var claimDoc = JsonDocument.Parse(await claimResp.Content.ReadAsStringAsync());
            claimDoc.RootElement.GetProperty("job_id").GetString().Should().Be(jobId);
        }

        using (var leased = Ops(HttpMethod.Get, $"/v1/ops/jobs/{jobId}"))
        {
            using var resp = await _client.SendAsync(leased);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("status").GetString().Should().Be("leased");
        }

        using (var cancel = Ops(HttpMethod.Post, $"/v1/ops/jobs/{jobId}/cancel",
                   """{"include_in_flight":true,"delete_artifacts":true}"""))
        {
            using var resp = await _client.SendAsync(cancel);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
        }

        using var complete = new HttpRequestMessage(HttpMethod.Post, $"/v1/jobs/{jobId}/complete")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        complete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
        using var completeResp = await _client.SendAsync(complete);
        // Late complete must not resurrect — 404 and status stays cancelled/failed.
        completeResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using (var get = Ops(HttpMethod.Get, $"/v1/ops/jobs/{jobId}"))
        {
            using var resp = await _client.SendAsync(get);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("status").GetString().Should().Be("failed");
            doc.RootElement.GetProperty("error").GetString().Should().Be("cancelled_by_ops");
        }
    }
}

public sealed class OpsModerationWebApplicationFactory : WebApplicationFactory<Program>
{
    public string ArtifactDir { get; } = Path.Combine(Path.GetTempPath(), $"ef-ops-art-{Guid.NewGuid():N}");
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ef-ops-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(ArtifactDir);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventForge:S3:LoadOnStartup"] = "false",
                ["EventForge:S3:Enabled"] = "false",
                ["EventForge:Artifacts:Enabled"] = "false",
                ["EventForge:SqlitePath"] = _dbPath,
                ["EventForge:LocalArtifactDir"] = ArtifactDir,
                ["EventForge:PublicUrl"] = "http://localhost",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* ignore */ }
        try { if (Directory.Exists(ArtifactDir)) Directory.Delete(ArtifactDir, true); } catch { /* ignore */ }
    }
}
