using EventForge.Infrastructure;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class GenQueueCapabilitiesTests
{
    [Theory]
    [InlineData("music", "wan")]
    [InlineData("ace-step", "wan")]
    [InlineData("ltx23-fp8", "ltx")]
    [InlineData("wan2", "wan")]
    public void ForModel_routes_music_to_wan_not_ltx(string model, string expected)
    {
        GenQueueCapabilities.ForModel(model).Should().Be(expected);
    }

    [Fact]
    public void ForProvisionMode_video_with_music_only_polls_wan()
    {
        GenQueueCapabilities.ForProvisionMode("video", wanEnabled: true, ltx23Enabled: false, musicEnabled: true)
            .Should().BeEquivalentTo(["wan"]);
    }
}
