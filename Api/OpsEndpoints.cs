using EventForge.Auth;
using EventForge.Core;
using EventForge.Queue;
using EventForge.Services;
using EventForge.WebSocket;

namespace EventForge.Api;

public static class OpsEndpoints
{
    public static void MapOpsEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/ops/snapshot", (
            HttpContext ctx,
            WorkerFleetTracker fleet,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            return Results.Ok(BuildSnapshot(fleet, queue));
        });

        app.MapGet("/v1/ops/fleet", (
            HttpContext ctx,
            WorkerFleetTracker fleet,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var snapshot = fleet.Snapshot();
            return Results.Ok(new
            {
                workers_total = snapshot.Workers.Count,
                workers_busy = snapshot.BusyCount,
                workers_idle = snapshot.IdleCount,
                workers_stale = snapshot.StaleCount,
                workers = snapshot.Workers,
            });
        });

        app.MapGet("/v1/ops/queue", (
            HttpContext ctx,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            return Results.Ok(BuildQueueStats(queue));
        });

        app.MapGet("/v1/ops/jobs/active", (
            HttpContext ctx,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var jobs = queue.SnapshotJobs()
                .Where(j => j.Status is JobStatus.Leased or JobStatus.Streaming)
                .OrderByDescending(j => j.LeasedUntil)
                .Select(ToJobDto)
                .ToList();
            return Results.Ok(new { count = jobs.Count, jobs });
        });

        app.MapGet("/v1/ops/failures", (
            HttpContext ctx,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth,
            int limit = 50) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            limit = Math.Clamp(limit, 1, 200);
            var jobs = queue.SnapshotJobs()
                .Where(j => j.Status == JobStatus.Failed)
                .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
                .Take(limit)
                .Select(ToJobDto)
                .ToList();
            return Results.Ok(new { count = jobs.Count, jobs });
        });


        app.MapPost("/v1/ops/jobs/purge-queued", async (
            HttpContext ctx,
            PurgeQueuedRequest body,
            JobService jobs,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.AppId))
                return Results.BadRequest(new { error = "app_id required" });

            var (removed, ids) = await jobs.PurgeQueuedForAppAsync(
                body.AppId,
                body.Capability,
                body.IncludeInFlight,
                body.DeleteS3,
                ct);

            return Results.Ok(new
            {
                app_id = body.AppId.Trim(),
                capability = body.Capability,
                removed,
                delete_s3 = body.DeleteS3,
                include_in_flight = body.IncludeInFlight,
                job_ids_sample = ids.Take(20).ToList(),
            });
        });

        app.MapGet("/v1/ops/workers/{workerId}", (
            HttpContext ctx,
            string workerId,
            WorkerFleetTracker fleet,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var worker = fleet.TryGetWorker(workerId);
            if (worker == null) return Results.NotFound();
            var activeJobs = queue.SnapshotJobs()
                .Where(j => j.Status is JobStatus.Leased or JobStatus.Streaming)
                .Where(j => string.Equals(j.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(j.WorkerHostname, worker.Hostname, StringComparison.OrdinalIgnoreCase))
                .Select(ToJobDto)
                .ToList();
            return Results.Ok(new { worker, active_jobs = activeJobs });
        });
    }

    internal static object BuildSnapshot(WorkerFleetTracker fleet, InMemoryJobQueue queue)
    {
        var fleetSnap = fleet.Snapshot();
        return new
        {
            generated_at = DateTimeOffset.UtcNow.ToString("O"),
            fleet = new
            {
                workers_total = fleetSnap.Workers.Count,
                workers_busy = fleetSnap.BusyCount,
                workers_idle = fleetSnap.IdleCount,
                workers_stale = fleetSnap.StaleCount,
                workers = fleetSnap.Workers,
            },
            queue = BuildQueueStats(queue),
            active_jobs = queue.SnapshotJobs()
                .Where(j => j.Status is JobStatus.Leased or JobStatus.Streaming)
                .OrderByDescending(j => j.LeasedUntil)
                .Take(100)
                .Select(ToJobDto)
                .ToList(),
            recent_failures = queue.SnapshotJobs()
                .Where(j => j.Status == JobStatus.Failed)
                .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
                .Take(25)
                .Select(ToJobDto)
                .ToList(),
        };
    }

    internal static object BuildQueueStats(InMemoryJobQueue queue)
    {
        var jobs = queue.SnapshotJobs();
        return new
        {
            jobs_total = jobs.Count,
            jobs_queued = jobs.Count(j => j.Status == JobStatus.Queued),
            jobs_in_progress = jobs.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
            jobs_completed = jobs.Count(j => j.Status == JobStatus.Completed),
            jobs_failed = jobs.Count(j => j.Status == JobStatus.Failed),
            by_capability = jobs
                .GroupBy(j => j.Capability, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    capability = g.Key,
                    queued = g.Count(j => j.Status == JobStatus.Queued),
                    in_progress = g.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                    failed = g.Count(j => j.Status == JobStatus.Failed),
                })
                .OrderBy(x => x.capability)
                .ToList(),
            by_tier = jobs
                .Where(j => j.Status == JobStatus.Queued)
                .GroupBy(j => j.Tier, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { tier = g.Key, queued = g.Count() })
                .OrderByDescending(x => x.queued)
                .ToList(),
        };
    }

    private static object ToJobDto(JobRecord j) => new
    {
        job_id = j.JobId,
        app_id = j.AppId,
        capability = j.Capability,
        tier = j.Tier,
        kind = j.Kind,
        status = j.Status,
        worker_id = j.WorkerId,
        hostname = j.WorkerHostname,
        created_at = j.CreatedAt.ToString("O"),
        leased_until = j.LeasedUntil?.ToString("O"),
        completed_at = j.CompletedAt?.ToString("O"),
        error = j.Error,
    };
}

public sealed class PurgeQueuedRequest
{
    public string AppId { get; set; } = "";
    public string? Capability { get; set; }
    public bool IncludeInFlight { get; set; } = true;
    public bool DeleteS3 { get; set; } = true;
}
