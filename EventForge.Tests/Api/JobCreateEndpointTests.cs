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
