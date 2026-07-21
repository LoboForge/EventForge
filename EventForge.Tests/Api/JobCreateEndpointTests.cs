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

public sealed class JobCreateEndpointTests : IClassFixture<EventForgeWebApplicationFactory>
{
    private readonly HttpClient _client;

    public JobCreateEndpointTests(EventForgeWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Post_v1_jobs_with_null_queue_priority_does_not_return_500()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = new StringContent(
                """
                {
                  "job_id": "regression-null-priority",
                  "capability": "ollama",
                  "tier": "bulk",
                  "kind": "text",
                  "queue_priority": null,
                  "payload": { "prompt": "regression" }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");

        using var resp = await _client.SendAsync(req);

        resp.StatusCode.Should().NotBe(HttpStatusCode.InternalServerError);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("job_id").GetString().Should().Be("regression-null-priority");
        doc.RootElement.GetProperty("status").GetString().Should().Be("queued");
    }

    [Fact]
    public async Task Empty_claim_reports_when_matching_jobs_are_blocked_by_missing_loras()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var hostname = $"loboforge-video-{suffix}";
        var jobId = $"blocked-lora-{suffix}";

        using (var checkIn = new HttpRequestMessage(HttpMethod.Post, "/v1/workers/check-in")
        {
            Content = new StringContent(
                $$"""
                {
                  "node_uuid": "{{suffix}}",
                  "hostname": "{{hostname}}",
                  "transport": "eventforge",
                  "forge_queue_capabilities": ["wan"],
                  "claim_ready_capabilities": ["wan"],
                  "known_loras": [],
                  "models": {
                    "unets": [
                      "Wan2.2/wan2.2_t2v_high_noise_14B_fp8_scaled.safetensors",
                      "Wan2.2/wan2.2_t2v_low_noise_14B_fp8_scaled.safetensors"
                    ],
                    "loras": []
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        })
        {
            checkIn.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var checkInResponse = await _client.SendAsync(checkIn);
            checkInResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using (var enqueue = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = new StringContent(
                $$"""
                {
                  "job_id": "{{jobId}}",
                  "capability": "wan",
                  "tier": "bulk",
                  "kind": "video",
                  "payload": {
                    "type": "assign_job",
                    "model": "wan2t2v",
                    "graph": {
                      "1": {
                        "class_type": "LoraLoaderModelOnly",
                        "inputs": {
                          "lora_name": "missing_for_worker.safetensors",
                          "model": ["0", 0]
                        }
                      }
                    }
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        })
        {
            enqueue.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "dev-local-key");
            using var enqueueResponse = await _client.SendAsync(enqueue);
            enqueueResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var claim = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs/claim")
        {
            Content = new StringContent(
                $$"""{"hostname":"{{hostname}}","capabilities":["wan"]}""",
                Encoding.UTF8,
                "application/json"),
        };
        claim.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
        using var claimResponse = await _client.SendAsync(claim);

        claimResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        claimResponse.Headers.GetValues("X-EventForge-Queued-Matching").Should().ContainSingle("1");
        claimResponse.Headers.GetValues("X-EventForge-Blocked-Lora").Should().ContainSingle("1");
        claimResponse.Headers.GetValues("X-EventForge-Missing-Loras")
            .Should().ContainSingle("missing_for_worker.safetensors");
    }
}

public sealed class EventForgeWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["EventForge:S3:LoadOnStartup"] = "false",
                ["EventForge:S3:Enabled"] = "false",
                ["EventForge:Artifacts:Enabled"] = "false",
            });
        });
    }
}
