using EventForge.Auth;
using EventForge.Queue;
using EventForge.Services;

namespace EventForge.Api;

public static class FleetEndpoints
{
    public static void MapFleetEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/queue/stats", (
            HttpContext ctx,
            InMemoryJobQueue queue,
            IApiKeyValidator appAuth,
            IWorkerKeyValidator workerAuth) =>
        {
            if (!TryAuthorizeMonitor(ctx, appAuth, workerAuth, out var appId, out _))
                return Results.Unauthorized();

            var jobs = queue.SnapshotJobs();
            if (!string.IsNullOrWhiteSpace(appId))
                jobs = jobs.Where(j => string.Equals(j.AppId, appId, StringComparison.OrdinalIgnoreCase)).ToList();

            var queued = jobs.Count(j => j.Status == Core.JobStatus.Queued);
            var leased = jobs.Count(j => j.Status is Core.JobStatus.Leased or Core.JobStatus.Streaming);
            var completed = jobs.Count(j => j.Status == Core.JobStatus.Completed);
            var failed = jobs.Count(j => j.Status == Core.JobStatus.Failed);

            return Results.Ok(new
            {
                jobs_total = jobs.Count,
                jobs_queued = queued,
                jobs_in_progress = leased,
                jobs_completed = completed,
                jobs_failed = failed,
                by_capability = jobs
                    .GroupBy(j => j.Capability, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        capability = g.Key,
                        queued = g.Count(j => j.Status == Core.JobStatus.Queued),
                        in_progress = g.Count(j => j.Status is Core.JobStatus.Leased or Core.JobStatus.Streaming),
                    })
                    .OrderBy(x => x.capability)
                    .ToList(),
            });
        });

        app.MapGet("/v1/fleet/workers", (
            HttpContext ctx,
            WorkerFleetTracker fleet,
            IApiKeyValidator appAuth,
            IWorkerKeyValidator workerAuth) =>
        {
            if (!TryAuthorizeMonitor(ctx, appAuth, workerAuth, out _, out _))
                return Results.Unauthorized();

            var snapshot = fleet.Snapshot();
            return Results.Ok(new
            {
                workers_total = snapshot.Workers.Count,
                workers_busy = snapshot.BusyCount,
                workers_idle = snapshot.IdleCount,
                workers = snapshot.Workers.Select(w => new
                {
                    worker_id = w.WorkerId,
                    hostname = w.Hostname,
                    capability = w.Capability,
                    capabilities = w.Capabilities,
                    claim_ready_capabilities = w.ClaimReadyCapabilities,
                    tier = w.Tier,
                    state = w.State,
                    active_job_id = w.ActiveJobId,
                    jobs_claimed = w.JobsClaimed,
                    jobs_completed = w.JobsCompleted,
                    jobs_failed = w.JobsFailed,
                    jobs_timed_out = w.JobsTimedOut,
                    jobs_released = w.JobsReleased,
                    last_seen_at = w.LastSeenAt,
                }).ToList(),
            });
        });
    }

    private static bool TryAuthorizeMonitor(
        HttpContext ctx,
        IApiKeyValidator appAuth,
        IWorkerKeyValidator workerAuth,
        out string? appId,
        out string? workerId)
    {
        appId = null;
        workerId = null;
        if (!AuthHelpers.TryReadApiKey(ctx, out var token)) return false;
        if (appAuth.TryValidate(token, out var validatedApp))
        {
            appId = validatedApp;
            return true;
        }
        if (workerAuth.TryValidate(token, out var validatedWorker))
        {
            workerId = validatedWorker;
            return true;
        }
        return false;
    }
}
