using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Services;

public sealed class WorkerFleetTrackerTests
{
    [Fact]
    public void RegisterCheckIn_stores_health_and_capabilities()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-1",
            Hostname = "wrath-image-123",
            GpuName = "RTX 4090",
            VramFree = 22000,
            VramTotal = 24576,
            Transport = "eventforge",
            ForgeQueueCapabilities = ["flux-klein", "chroma"],
            Busy = false,
        });

        var snap = fleet.TryGetWorker("node-1");
        snap.Should().NotBeNull();
        snap!.Hostname.Should().Be("wrath-image-123");
        snap.GpuName.Should().Be("RTX 4090");
        snap.Transport.Should().Be("eventforge");
        snap.Capabilities.Should().Contain("flux-klein");
        snap.CheckInStale.Should().BeFalse();
    }

    [Fact]
    public void OnClaim_marks_worker_busy_with_job()
    {
        var fleet = new WorkerFleetTracker();
        fleet.OnClaim("wrath", "wrath-box", "flux-klein", "normal", "job-abc");

        var snap = fleet.Snapshot();
        snap.BusyCount.Should().Be(1);
        snap.Workers[0].ActiveJobId.Should().Be("job-abc");
        snap.Workers[0].State.Should().Be("busy");
    }

    [Fact]
    public void RegisterCheckIn_keys_by_node_uuid_when_auth_worker_shared()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-a",
            Hostname = "loboforge-image-111",
            Transport = "eventforge",
        });
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-b",
            Hostname = "loboforge-image-222",
            Transport = "eventforge",
        });

        fleet.Snapshot().Workers.Should().HaveCount(2);
        fleet.TryGetWorker("node-a")!.Hostname.Should().Be("loboforge-image-111");
        fleet.TryGetWorker("node-b")!.Hostname.Should().Be("loboforge-image-222");
    }

    [Fact]
    public void OnClaim_after_checkin_reuses_node_uuid_row_not_hostname_duplicate()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-a",
            Hostname = "loboforge-image-111",
            Transport = "eventforge",
            Busy = false,
        });

        fleet.OnClaim("wrath", "loboforge-image-111", "flux-klein", "normal", "job-abc");

        fleet.Snapshot().Workers.Should().HaveCount(1);
        var row = fleet.TryGetWorker("node-a");
        row.Should().NotBeNull();
        row!.State.Should().Be("busy");
        row.ActiveJobId.Should().Be("job-abc");
        row.Hostname.Should().Be("loboforge-image-111");
        fleet.TryGetWorker("loboforge-image-111").Should().BeNull();
    }

    [Fact]
    public void OnComplete_after_checkin_and_claim_clears_same_row()
    {
        var fleet = new WorkerFleetTracker();
        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-b",
            Hostname = "loboforge-video-222",
            Transport = "eventforge",
        });
        fleet.OnClaim("wrath", "loboforge-video-222", "wan", "normal", "job-xyz");
        fleet.OnComplete("wrath", "loboforge-video-222");

        fleet.Snapshot().Workers.Should().HaveCount(1);
        var row = fleet.TryGetWorker("node-b");
        row!.State.Should().Be("idle");
        row.ActiveJobId.Should().BeNull();
        row.JobsCompleted.Should().Be(1);
    }

    [Fact]
    public void RegisterCheckIn_removes_stale_hostname_row_when_node_uuid_checkin_arrives()
    {
        var fleet = new WorkerFleetTracker();
        fleet.OnClaim("wrath", "loboforge-image-111", "flux-klein", "normal", "job-abc");

        fleet.RegisterCheckIn("wrath", new WorkerCheckInPayload
        {
            NodeUuid = "node-a",
            Hostname = "loboforge-image-111",
            Transport = "eventforge",
            ForgeQueueCapabilities = ["flux-klein"],
            ClaimReadyCapabilities = ["flux-klein"],
        });

        fleet.Snapshot().Workers.Should().HaveCount(1);
        fleet.TryGetWorker("node-a")!.ClaimReadyCapabilities.Should().Contain("flux-klein");
        fleet.TryGetWorker("loboforge-image-111").Should().BeNull();
    }

    [Fact]
    public void OnClaim_without_prior_checkin_keys_by_hostname()
    {
        var fleet = new WorkerFleetTracker();
        fleet.OnClaim("wrath", "wrath-box", "flux-klein", "normal", "job-abc");

        fleet.Snapshot().Workers.Should().HaveCount(1);
        fleet.TryGetWorker("wrath-box").Should().NotBeNull();
    }
}
