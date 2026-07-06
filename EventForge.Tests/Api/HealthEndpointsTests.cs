using EventForge.Api;
using EventForge.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EventForge.Tests.Api;

public sealed class HealthEndpointsTests
{
    [Fact]
    public void TryAuthorizeOps_without_ops_key_is_not_authorized()
    {
        var ctx = new DefaultHttpContext();
        var opsAuth = new ConfigOpsKeyValidator(
            Microsoft.Extensions.Options.Options.Create(new EventForge.Configuration.EventForgeOptions
            {
                OpsKey = "expected-ops-key",
            }));

        AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out var authorized).Should().BeFalse();
        authorized.Should().BeFalse();
    }

    [Fact]
    public void TryAuthorizeOps_accepts_ops_header()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-EventForge-Ops-Key"] = "expected-ops-key";
        var opsAuth = new ConfigOpsKeyValidator(
            Microsoft.Extensions.Options.Options.Create(new EventForge.Configuration.EventForgeOptions
            {
                OpsKey = "expected-ops-key",
            }));

        AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out var authorized).Should().BeTrue();
        authorized.Should().BeTrue();
    }
}
