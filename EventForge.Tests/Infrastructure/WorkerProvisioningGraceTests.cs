using EventForge.Infrastructure;
using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class WorkerProvisioningGraceTests
{
    private static WorkerSnapshot GenWorker(bool withinGrace, bool stale = false) => new()
    {
        Hostname = "loboforge-video-45256646",
        Capabilities = ["wan"],
        ClaimReadyCapabilities = [],
        Busy = false,
        CheckInStale = stale,
        WithinProvisioningGrace = withinGrace,
    };

    [Fact]
    public void Provisioning_box_gets_benign_badge_not_reap_worthy_badges()
    {
        var badges = WorkerContribution.Badges(GenWorker(withinGrace: true));

        badges.Should().Contain("provisioning");
        badges.Should().NotContain("no-claim-ready");
        badges.Should().NotContain("idle-no-jobs");
        badges.Should().NotContain("wan-not-ready");
    }

    [Fact]
    public void Wan_native_box_within_grace_is_not_flagged_wan_not_ready()
    {
        var worker = new WorkerSnapshot
        {
            Hostname = "loboforge-wan-native-45256646",
            Capabilities = ["wan"],
            ClaimReadyCapabilities = [],
            Busy = false,
            CheckInStale = false,
            WithinProvisioningGrace = true,
        };

        var badges = WorkerContribution.Badges(worker);
        badges.Should().Contain("provisioning");
        badges.Should().NotContain("wan-not-ready");
    }

    [Fact]
    public void Box_past_grace_is_flagged_not_ready()
    {
        var badges = WorkerContribution.Badges(GenWorker(withinGrace: false));

        badges.Should().NotContain("provisioning");
        badges.Should().Contain("no-claim-ready");
        badges.Should().Contain("idle-no-jobs");
    }

    [Fact]
    public void Stale_box_is_not_treated_as_provisioning()
    {
        // A box that stopped checking in is a real problem even inside the grace window.
        var badges = WorkerContribution.Badges(GenWorker(withinGrace: true, stale: true));

        badges.Should().NotContain("provisioning");
        badges.Should().Contain("stale");
    }

    [Fact]
    public void Fresh_check_in_is_within_provisioning_grace()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-fresh-1",
            Hostname = "loboforge-video-45256646",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = [],
            Busy = false,
        });

        var snap = fleet.TryGetWorker("node-fresh-1")!;
        snap.WithinProvisioningGrace.Should().BeTrue();
        snap.AgeSeconds.Should().BeLessThan(60);
        snap.FirstSeenAt.Should().NotBeNullOrWhiteSpace();
        // Provisioning boxes are non-contributing but must not carry reap-worthy badges.
        WorkerContribution.Badges(snap).Should().Contain("provisioning");
    }

    [Fact]
    public void First_seen_is_preserved_across_check_ins()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-fresh-2",
            Hostname = "loboforge-video-99",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = [],
        });
        var first = fleet.TryGetWorker("node-fresh-2")!.FirstSeenAt;

        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-fresh-2",
            Hostname = "loboforge-video-99",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["wan"],
            ClaimReadyCapabilities = ["wan"],
        });

        fleet.TryGetWorker("node-fresh-2")!.FirstSeenAt.Should().Be(first);
    }
}
