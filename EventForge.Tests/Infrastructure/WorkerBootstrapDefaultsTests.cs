using EventForge.Configuration;
using EventForge.Infrastructure;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public class WorkerBootstrapDefaultsTests
{
    [Fact]
    public void BuildEventForgeExtraEnvTemplate_uses_worker_bearer_token_not_worker_id()
    {
        var opts = new EventForgeOptions
        {
            PublicUrl = "https://eventforge.loboforge.com",
            WorkerKeys = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["wrath-ef-ff4ec2ee76871a822c5fc9e8"] = "wrath",
            },
        };

        var template = WorkerBootstrapDefaults.BuildEventForgeExtraEnvTemplate(opts, "image");

        template.Should().Contain("EVENT_FORGE_WORKER_KEY=wrath-ef-ff4ec2ee76871a822c5fc9e8");
        template.Should().NotContain("EVENT_FORGE_WORKER_KEY=wrath\n");
    }
}
