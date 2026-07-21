using EventForge.Core;
using EventForge.Queue;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Queue;

public sealed class TryClaimAnyTests
{
    private static JobRecord MakeJob(
        string id,
        string capability,
        string tier,
        DateTimeOffset createdAt,
        int? queuePriority = null) => new()
    {
        JobId = id,
        AppId = "test-app",
        Capability = capability,
        Tier = tier,
        QueuePriority = queuePriority,
        Kind = JobKind.Image,
        PayloadJson = "{}",
        Status = JobStatus.Queued,
        CreatedAt = createdAt,
    };

    [Fact]
    public void TryClaimAny_prefers_admin_over_vip_over_normal_over_bulk()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("bulk", "flux", "bulk", t0));
        queue.Enqueue(MakeJob("normal", "flux", "normal", t0.AddSeconds(1)));
        queue.Enqueue(MakeJob("vip", "flux", "vip", t0.AddSeconds(2)));
        queue.Enqueue(MakeJob("admin", "flux", "admin", t0.AddSeconds(3)));

        var claimed = queue.TryClaimAny(["flux"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("admin");
    }

    [Fact]
    public void TryClaimAny_fifo_within_same_tier()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("second", "flux", "normal", t0.AddSeconds(10)));
        queue.Enqueue(MakeJob("first", "flux", "normal", t0));

        var claimed = queue.TryClaimAny(["flux"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("first");
    }

    [Fact]
    public void TryClaimAny_matches_any_supported_capability()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("other-cap", "flux-dev", "admin", t0));
        queue.Enqueue(MakeJob("match", "flux-klein", "bulk", t0.AddSeconds(1)));

        var claimed = queue.TryClaimAny(["flux-klein", "caption"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("match");
    }

    [Fact]
    public void TryClaimAny_higher_queue_priority_wins_within_tier()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("low-pri", "flux", "normal", t0, queuePriority: 0));
        queue.Enqueue(MakeJob("high-pri", "flux", "normal", t0.AddSeconds(1), queuePriority: 5));

        var claimed = queue.TryClaimAny(["flux"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("high-pri");
    }

    [Fact]
    public void TryClaimAny_tier_beats_queue_priority()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("bulk-high", "flux", "bulk", t0, queuePriority: 99));
        queue.Enqueue(MakeJob("normal-low", "flux", "normal", t0.AddSeconds(1), queuePriority: 0));

        var claimed = queue.TryClaimAny(["flux"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("normal-low");
    }

    [Fact]
    public void TryClaimAny_returns_null_when_no_capability_match()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MakeJob("only-flux", "flux", "admin", DateTimeOffset.UtcNow));

        var claimed = queue.TryClaimAny(["caption"], "worker-1", "host-1", TimeSpan.FromMinutes(5));

        claimed.Should().BeNull();
    }

    private sealed class FixedRandom : Random
    {
        private readonly int _value;
        public FixedRandom(int value) => _value = value;
        public override int Next(int maxValue) => _value;
    }

    [Fact]
    public void TryClaimAny_random_bulk_picks_among_bulk_not_fifo()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("oldest-bulk", "flux", "bulk", t0));
        queue.Enqueue(MakeJob("mid-bulk", "flux", "bulk", t0.AddSeconds(1)));
        queue.Enqueue(MakeJob("newest-bulk", "flux", "bulk", t0.AddSeconds(2)));

        // Fixed index 2 → third peer (newest-bulk), proving we are not FIFO.
        var claimed = queue.TryClaimAny(
            ["flux"],
            "worker-1",
            "host-1",
            TimeSpan.FromMinutes(5),
            isRandomBulkApp: _ => true,
            rng: new FixedRandom(2));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("newest-bulk");
    }

    [Fact]
    public void TryClaimAny_random_bulk_still_prefers_higher_tier()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(MakeJob("bulk-a", "flux", "bulk", t0));
        queue.Enqueue(MakeJob("bulk-b", "flux", "bulk", t0.AddSeconds(1)));
        queue.Enqueue(MakeJob("normal", "flux", "normal", t0.AddSeconds(2)));

        var claimed = queue.TryClaimAny(
            ["flux"],
            "worker-1",
            "host-1",
            TimeSpan.FromMinutes(5),
            isRandomBulkApp: _ => true,
            rng: new FixedRandom(1));

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("normal");
    }

    [Fact]
    public void TryClaimAny_random_bulk_only_affects_flagged_app()
    {
        var queue = new InMemoryJobQueue();
        var t0 = DateTimeOffset.UtcNow;
        queue.Enqueue(new JobRecord
        {
            JobId = "fifo-old",
            AppId = "fifo-app",
            Capability = "flux",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = t0,
        });
        queue.Enqueue(new JobRecord
        {
            JobId = "random-new",
            AppId = "random-app",
            Capability = "flux",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = t0.AddSeconds(5),
        });

        var claimed = queue.TryClaimAny(
            ["flux"],
            "worker-1",
            "host-1",
            TimeSpan.FromMinutes(5),
            isRandomBulkApp: appId => string.Equals(appId, "random-app", StringComparison.OrdinalIgnoreCase),
            rng: new FixedRandom(0));

        // Older FIFO app still wins the priority race before randomize applies.
        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("fifo-old");
    }
}
