using EventForge.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EventForge.Tests.Configuration;

public class EventForgeSecretsBinderTests
{
    [Fact]
    public void Apply_inherits_root_vast_workers_and_hf_from_loboforge_shaped_secrets()
    {
        var json = """
            {
              "VastAi": { "ApiKey": "vast-root-key" },
              "Workers": { "Secret": "gpu-worker-secret" },
              "HuggingFace": { "Token": "hf-token" },
              "LoboForge": { "BaseUrl": "https://www.loboforge.com" },
              "EventForge": {
                "ApiKey": "app-key",
                "WorkerKey": "ef-worker-key",
                "OpsKey": "ops-key",
                "BaseUrl": "https://eventforge.loboforge.com"
              }
            }
            """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var opts = new EventForgeOptions();

        EventForgeSecretsBinder.Apply(config, opts);

        opts.VastAi.ApiKey.Should().Be("vast-root-key");
        opts.WorkerSecret.Should().Be("gpu-worker-secret");
        opts.HuggingFaceToken.Should().Be("hf-token");
        opts.PublicUrl.Should().Be("https://eventforge.loboforge.com");
        opts.AgentScriptBaseUrl.Should().Be("https://www.loboforge.com");
        opts.OpsKey.Should().Be("ops-key");
        opts.ApiKeys.Should().ContainKey("app-key");
        opts.WorkerKeys.Should().ContainKey("ef-worker-key");
    }

    [Fact]
    public void Apply_prefers_eventforge_nested_vast_key_over_root()
    {
        var json = """
            {
              "VastAi": { "ApiKey": "root-key" },
              "EventForge": {
                "VastAi": { "ApiKey": "nested-key" }
              }
            }
            """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var opts = new EventForgeOptions { VastAi = new VastAiOptions { ApiKey = "nested-key" } };

        EventForgeSecretsBinder.Apply(config, opts);

        opts.VastAi.ApiKey.Should().Be("nested-key");
    }
}
