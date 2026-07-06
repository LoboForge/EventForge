using EventForge.Core;
using EventForge.Queue;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Queue;

public sealed class TryExtendLeaseTests
{
    [Fact]
    public void TryExtendLease_renews_active_job_for_same_worker()
    {
        var queue = new InMemoryJobQueue();
        var job = new JobRecord
        {
            JobId = "job-1",
            AppId = "app",
            Capability = "wan",
            Tier = "normal",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        queue.Enqueue(job);
        var claimed = queue.TryClaimAny(["wan"], "wrath", "host-1", TimeSpan.FromSeconds(60));
        claimed.Should().NotBeNull();
        claimed!.LeasedUntil.Should().NotBeNull();

        var before = claimed.LeasedUntil;
        Thread.Sleep(50);
        queue.TryExtendLease("job-1", "wrath", TimeSpan.FromMinutes(15)).Should().BeTrue();
        var after = queue.Get("job-1");
        after!.LeasedUntil.Should().BeAfter(before!.Value);
    }

    [Fact]
    public void TryExtendLease_rejects_wrong_worker()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(new JobRecord
        {
            JobId = "job-2",
            AppId = "app",
            Capability = "ltx",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        queue.TryClaimAny(["ltx"], "wrath", "host-1", TimeSpan.FromSeconds(60));
        queue.TryExtendLease("job-2", "other-worker", TimeSpan.FromMinutes(15)).Should().BeFalse();
    }

    [Fact]
    public void Enqueue_is_idempotent_for_already_queued_job()
    {
        var queue = new InMemoryJobQueue();
        var job = new JobRecord
        {
            JobId = "dup",
            AppId = "app",
            Capability = "flux-klein",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        queue.Enqueue(job);
        queue.Enqueue(job);
        queue.QueuedCount.Should().Be(1);
    }
}
