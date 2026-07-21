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

public sealed class LoraAssetEndpointTests : IClassFixture<LoraAssetWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LoraAssetEndpointTests(LoraAssetWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Upload_list_download_and_delete_lora_via_proxy()
    {
        var fileName = $"test_style_{Guid.NewGuid():N}.safetensors";
        var payload = Encoding.UTF8.GetBytes("x" + new string('a', 2048));

        using (var beginReq = new HttpRequestMessage(HttpMethod.Post, "/v1/assets/loras")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    file_name = fileName,
                    modes = "image",
                    bytes = payload.Length,
                    replace = true,
                }),
                Encoding.UTF8,
                "application/json"),
        })
        {
            beginReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var beginResp = await _client.SendAsync(beginReq);
            beginResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var beginDoc = JsonDocument.Parse(await beginResp.Content.ReadAsStringAsync());
            var assetId = beginDoc.RootElement.GetProperty("asset_id").GetString();
            assetId.Should().NotBeNullOrWhiteSpace();
            var uploadUrl = beginDoc.RootElement.GetProperty("upload").GetProperty("url").GetString();
            uploadUrl.Should().Contain($"/v1/assets/loras/{assetId}/content");

            using var putReq = new HttpRequestMessage(HttpMethod.Put, $"/v1/assets/loras/{assetId}/content")
            {
                Content = new ByteArrayContent(payload),
            };
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            putReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var putResp = await _client.SendAsync(putReq);
            putResp.StatusCode.Should().Be(HttpStatusCode.OK);

            using var completeReq = new HttpRequestMessage(HttpMethod.Post, $"/v1/assets/loras/{assetId}/complete")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            completeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var completeResp = await _client.SendAsync(completeReq);
            completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var completeDoc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
            completeDoc.RootElement.GetProperty("status").GetString().Should().Be("ready");
        }

        // Enqueue a job that references the LoRA so workers can download by job id.
        var jobId = $"lora-job-{Guid.NewGuid():N}";
        using (var jobReq = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    job_id = jobId,
                    capability = "flux-klein",
                    tier = "bulk",
                    kind = "image",
                    payload = new
                    {
                        type = "assign_job",
                        model = "flux-klein",
                        graph = new Dictionary<string, object>
                        {
                            ["1"] = new
                            {
                                class_type = "LoraLoader",
                                inputs = new { lora_name = fileName },
                            },
                        },
                    },
                }),
                Encoding.UTF8,
                "application/json"),
        })
        {
            jobReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var jobResp = await _client.SendAsync(jobReq);
            jobResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var workerSuffix = Guid.NewGuid().ToString("N");
        var hostname = $"loboforge-image-{workerSuffix}";
        async Task CheckInAsync(bool hasLora)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/workers/check-in")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        node_uuid = workerSuffix,
                        hostname,
                        transport = "eventforge",
                        forge_queue_capabilities = new[] { "flux-klein" },
                        claim_ready_capabilities = new[] { "flux-klein" },
                        known_loras = hasLora ? new[] { fileName } : Array.Empty<string>(),
                        models = new
                        {
                            unets = new[] { "flux-klein-test.safetensors" },
                            loras = hasLora ? new[] { fileName } : Array.Empty<string>(),
                        },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var resp = await _client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await CheckInAsync(hasLora: false);

        // A ready server asset is offered for proactive idle prefetch, but is
        // deliberately NOT enough to lease the job. The worker must first
        // check in with the validated file in known_loras.
        using (var neededReq = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/v1/workers/loras/needed?hostname={hostname}&capabilities=flux-klein"))
        {
            neededReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var neededResp = await _client.SendAsync(neededReq);
            neededResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var neededDoc = JsonDocument.Parse(await neededResp.Content.ReadAsStringAsync());
            var needed = neededDoc.RootElement.GetProperty("loras").EnumerateArray().ToList();
            needed.Should().ContainSingle();
            needed[0].GetProperty("job_id").GetString().Should().Be(jobId);
            needed[0].GetProperty("file_name").GetString().Should().Be(fileName);
        }

        async Task<HttpStatusCode> ClaimAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/jobs/claim")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        hostname,
                        capabilities = new[] { "flux-klein" },
                    }),
                    Encoding.UTF8,
                    "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var resp = await _client.SendAsync(req);
            return resp.StatusCode;
        }

        (await ClaimAsync()).Should().Be(HttpStatusCode.NoContent);
        await CheckInAsync(hasLora: true);
        (await ClaimAsync()).Should().Be(HttpStatusCode.OK);

        using (var dlReq = new HttpRequestMessage(HttpMethod.Get, $"/v1/jobs/{jobId}/loras/{fileName}"))
        {
            dlReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrath-worker-key");
            using var dlResp = await _client.SendAsync(dlReq);
            dlResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var bytes = await dlResp.Content.ReadAsByteArrayAsync();
            bytes.Length.Should().Be(payload.Length);
        }

        using (var listReq = new HttpRequestMessage(HttpMethod.Get, "/v1/assets/loras"))
        {
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var listResp = await _client.SendAsync(listReq);
            listResp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
            listDoc.RootElement.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
        }

        // Cleanup via list + delete matching file
        using (var listReq = new HttpRequestMessage(HttpMethod.Get, "/v1/assets/loras?status=ready"))
        {
            listReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
            using var listResp = await _client.SendAsync(listReq);
            using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
            foreach (var row in listDoc.RootElement.GetProperty("loras").EnumerateArray())
            {
                if (!string.Equals(row.GetProperty("file_name").GetString(), fileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var id = row.GetProperty("asset_id").GetString();
                using var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/v1/assets/loras/{id}");
                delReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
                using var delResp = await _client.SendAsync(delReq);
                delResp.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }
    }

    [Fact]
    public async Task Rejects_non_safetensors_file_name()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/assets/loras")
        {
            Content = new StringContent(
                """{"file_name":"evil.bin","modes":"all"}""",
                Encoding.UTF8,
                "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "loboforge-local-key");
        using var resp = await _client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

public sealed class LoraAssetWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ef-lora-test-{Guid.NewGuid():N}.db");
    private readonly string _artifactDir = Path.Combine(Path.GetTempPath(), $"ef-lora-art-{Guid.NewGuid():N}");

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
                ["EventForge:LoraAssets:Enabled"] = "false",
                ["EventForge:LoraAssets:MinReadyBytes"] = "1",
                ["EventForge:SqlitePath"] = _dbPath,
                ["EventForge:LocalArtifactDir"] = _artifactDir,
                ["EventForge:PublicUrl"] = "http://localhost",
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_artifactDir)) Directory.Delete(_artifactDir, true); } catch { }
    }
}
