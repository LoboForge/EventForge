using EventForge.Infrastructure;
using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class WorkerClaimPolicyTests
{
    private static WorkerSnapshot ReadyWorker(params string[] claimReady) => new()
    {
        Hostname = "loboforge-ltx-43579394",
        ClaimReadyCapabilities = claimReady,
        CheckInStale = false,
        ModelsJson = """{"checkpoints":["ltx-2.3-22b-distilled.safetensors"]}""",
    };

    [Fact]
    public void ClaimableCapabilities_uses_only_check_in_claim_ready()
    {
        var worker = ReadyWorker("ltx");
        WorkerClaimPolicy.ClaimableCapabilities(worker).Should().Equal("ltx");
    }

    [Fact]
    public void ClaimableCapabilities_empty_when_claim_ready_empty()
    {
        var worker = ReadyWorker();
        WorkerClaimPolicy.ClaimableCapabilities(worker).Should().BeEmpty();
        WorkerClaimPolicy.CanAttemptClaim(worker).Should().BeFalse();
    }

    [Fact]
    public void ClaimableCapabilities_empty_when_check_in_stale()
    {
        var worker = new WorkerSnapshot
        {
            Hostname = "loboforge-image-1",
            ClaimReadyCapabilities = ["flux-klein"],
            CheckInStale = true,
        };
        WorkerClaimPolicy.ClaimableCapabilities(worker).Should().BeEmpty();
    }

    [Fact]
    public void ClaimableCapabilities_ignores_request_body_capabilities()
    {
        var worker = ReadyWorker("ltx");
        // Policy never intersects with client request — check-in is authoritative.
        WorkerClaimPolicy.ClaimableCapabilities(worker).Should().NotContain("flux-klein");
    }
}
