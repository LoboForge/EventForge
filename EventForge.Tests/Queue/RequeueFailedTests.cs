using EventForge.Core;
using EventForge.Queue;
using Xunit;

namespace EventForge.Tests.Queue;

public sealed class RequeueFailedTests
{
    [Fact]
    public void RequeueFailedWhere_moves_matching_jobs_to_queue_tail()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MakeJob("a", JobStatus.Failed, "wan", error: "missing LoRAs"));
        queue.Enqueue(MakeJob("b", JobStatus.Queued, "wan"));
        queue.Enqueue(MakeJob("c", JobStatus.Failed, "image", error: "boom"));
        queue.Enqueue(MakeJob("d", JobStatus.Failed, "wan", error: "layout.json missing"));

        var requeued = queue.RequeueFailedWhere(j =>
            j.Status == JobStatus.Failed
            && string.Equals(j.Capability, "wan", StringComparison.OrdinalIgnoreCase)
            && (j.Error ?? "").Contains("LoRAs", StringComparison.OrdinalIgnoreCase));

        Assert.Single(requeued);
        Assert.Equal("a", requeued[0].JobId);
        Assert.Equal(2, queue.SnapshotJobs().Count(j => j.Status == JobStatus.Queued));
        Assert.Equal(JobStatus.Queued, queue.Get("a")!.Status);
        Assert.Null(queue.Get("a")!.Error);
        Assert.Equal(JobStatus.Failed, queue.Get("c")!.Status);
        Assert.Equal(JobStatus.Failed, queue.Get("d")!.Status);
    }

    [Fact]
    public void RequeueFailedWhere_respects_limit()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MakeJob("a", JobStatus.Failed, "wan"));
        queue.Enqueue(MakeJob("b", JobStatus.Failed, "wan"));

        var requeued = queue.RequeueFailedWhere(_ => true, limit: 1);

        Assert.Single(requeued);
        Assert.Equal(1, queue.SnapshotJobs().Count(j => j.Status == JobStatus.Failed));
        Assert.Equal(1, queue.SnapshotJobs().Count(j => j.Status == JobStatus.Queued));
    }

    private static JobRecord MakeJob(string id, string status, string capability, string? error = null) =>
        new()
        {
            JobId = id,
            AppId = "app",
            Capability = capability,
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = "{}",
            Status = status,
            Error = error,
            CompletedAt = status == JobStatus.Failed ? DateTimeOffset.UtcNow : null,
        };
}
