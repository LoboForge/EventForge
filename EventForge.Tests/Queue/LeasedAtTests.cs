using EventForge.Core;
using EventForge.Queue;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Queue;

public sealed class LeasedAtTests
{
    [Fact]
    public void TryClaimAny_sets_leased_at_and_clears_on_release()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(new JobRecord
        {
            JobId = "job-leased-at",
            AppId = "app",
            Capability = "wan",
            Tier = "normal",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var before = DateTimeOffset.UtcNow;
        var claimed = queue.TryClaimAny(["wan"], "worker-1", "host-video", TimeSpan.FromMinutes(5));
        var after = DateTimeOffset.UtcNow;

        claimed.Should().NotBeNull();
        claimed!.LeasedAt.Should().NotBeNull();
        claimed.LeasedAt!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);

        queue.TryRelease("job-leased-at", "worker-1").Should().BeTrue();
        queue.Get("job-leased-at")!.LeasedAt.Should().BeNull();
    }
}
