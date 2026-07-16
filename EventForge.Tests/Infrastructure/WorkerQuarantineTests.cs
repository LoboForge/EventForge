using EventForge.Infrastructure;
using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class WorkerQuarantineTests
{
    [Fact]
    public void Quarantine_clears_claim_ready_and_blocks_policy()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-wan-1",
            Hostname = "loboforge-wan-native-123",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = ["wan"],
            Busy = false,
        });

        var before = fleet.TryGetWorker("node-wan-1");
        before.Should().NotBeNull();
        WorkerClaimPolicy.ClaimableCapabilities(before).Should().Contain("wan");

        var q = fleet.Quarantine("loboforge-wan-native-123", "maintenance_missing_librosa", "ops-test");
        q.Should().NotBeNull();
        q!.Quarantined.Should().BeTrue();
        q.QuarantineReason.Should().Be("maintenance_missing_librosa");
        q.ClaimReadyCapabilities.Should().BeEmpty();
        WorkerClaimPolicy.ClaimableCapabilities(q).Should().BeEmpty();
        WorkerContribution.Badges(q).Should().Contain("quarantined");
    }

    [Fact]
    public void CheckIn_while_quarantined_keeps_claim_ready_empty()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-wan-2",
            Hostname = "loboforge-wan-native-456",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = ["wan"],
        });
        fleet.Quarantine("node-wan-2", "broken_deps");

        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-wan-2",
            Hostname = "loboforge-wan-native-456",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = ["wan"],
            Busy = false,
        });

        var snap = fleet.TryGetWorker("node-wan-2")!;
        snap.Quarantined.Should().BeTrue();
        snap.ClaimReadyCapabilities.Should().BeEmpty();
        WorkerClaimPolicy.ClaimableCapabilities(snap).Should().BeEmpty();
    }

    [Fact]
    public void Unquarantine_allows_claim_ready_on_next_check_in()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-wan-3",
            Hostname = "loboforge-wan-native-789",
            Transport = "eventforge",
            ClaimReadyCapabilities = ["wan"],
            ForgeQueueCapabilities = ["wan"],
        });
        fleet.Quarantine("node-wan-3", "tmp");
        fleet.Unquarantine("node-wan-3");

        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-wan-3",
            Hostname = "loboforge-wan-native-789",
            Transport = "eventforge",
            ClaimReadyCapabilities = ["wan"],
            ForgeQueueCapabilities = ["wan"],
        });

        var snap = fleet.TryGetWorker("node-wan-3")!;
        snap.Quarantined.Should().BeFalse();
        WorkerClaimPolicy.ClaimableCapabilities(snap).Should().Contain("wan");
    }

    [Theory]
    [InlineData("maintenance_missing_librosa", true)]
    [InlineData("maintenance_missing_decord", true)]
    [InlineData("generations_exhausted", false)]
    [InlineData("billing_hold", false)]
    public void Maintenance_pause_reasons_are_detected(string reason, bool expected)
    {
        EventForge.Api.OpsEndpoints.IsWorkerMaintenancePauseReason(reason).Should().Be(expected);
    }
}
