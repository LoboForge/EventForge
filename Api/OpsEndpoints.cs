using EventForge.Auth;
using EventForge.Core;
using EventForge.Infrastructure;
using EventForge.Persistence;
using EventForge.Queue;
using EventForge.Services;
using EventForge.Storage;
using EventForge.WebSocket;
using System.Text.Json.Serialization;

namespace EventForge.Api;

public static class OpsEndpoints
{
    public static void MapOpsEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/ops/snapshot", (
            HttpContext ctx,
            WorkerFleetTracker fleet,
            InMemoryJobQueue queue,
            ConsumerAppRegistry apps,
            OpsMetricsHistory history,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var snap = BuildSnapshot(fleet, queue, apps);
            RecordMetrics(history, fleet, queue);
            return Results.Ok(snap);
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
                .OrderBy(j => j.LeasedAt ?? j.CreatedAt)
                .Select(ToJobDto)
                .ToList();
            return Results.Ok(new { count = jobs.Count, jobs });
        });

        // Literal job action routes must register before /v1/ops/jobs/{jobId} or POST returns 405.
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

        app.MapPost("/v1/ops/jobs/reassign-consumer", (
            HttpContext ctx,
            ReassignConsumerRequest body,
            JobService jobs,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.FromAppId) || string.IsNullOrWhiteSpace(body.ToAppId))
                return Results.BadRequest(new { error = "from_app_id and to_app_id required" });

            var reassigned = jobs.ReassignConsumer(
                body.FromAppId,
                body.ToAppId,
                body.Capability,
                body.Status,
                body.OrchestratorCaptionOnly);

            return Results.Ok(new
            {
                from_app_id = body.FromAppId.Trim(),
                to_app_id = body.ToAppId.Trim(),
                capability = body.Capability,
                status = body.Status ?? "queued",
                orchestrator_caption_only = body.OrchestratorCaptionOnly,
                reassigned,
            });
        });

        app.MapPost("/v1/ops/jobs/retier", async (
            HttpContext ctx,
            RetierJobsRequest body,
            JobService jobs,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.FromTier) || string.IsNullOrWhiteSpace(body.ToTier))
                return Results.BadRequest(new { error = "from_tier and to_tier required" });

            var retiered = await jobs.RetierQueuedAsync(
                body.AppId,
                body.Capability,
                body.FromTier,
                body.ToTier,
                ct);

            return Results.Ok(new
            {
                retiered,
                from_tier = body.FromTier.Trim(),
                to_tier = body.ToTier.Trim(),
                app_id = body.AppId,
            });
        });

        app.MapPost("/v1/ops/jobs/cancel-matching", async (
            HttpContext ctx,
            CancelMatchingRequest body,
            JobService jobs,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.AppId))
                return Results.BadRequest(new { error = "app_id required" });

            var (cancelled, ids) = await jobs.CancelMatchingQueuedAsync(
                body.AppId,
                body.Capability,
                body.ExternalIdContains,
                body.PayloadContains,
                body.IncludeInFlight,
                ct);

            return Results.Ok(new
            {
                app_id = body.AppId.Trim(),
                capability = body.Capability,
                external_id_contains = body.ExternalIdContains,
                payload_contains = body.PayloadContains,
                cancelled,
                include_in_flight = body.IncludeInFlight,
                job_ids_sample = ids.Take(20).ToList(),
            });
        });

        app.MapPost("/v1/ops/jobs/requeue-failed", async (
            HttpContext ctx,
            RequeueFailedRequest body,
            JobService jobs,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();

            var (requeued, ids) = await jobs.RequeueFailedAsync(
                body.Capability,
                body.AppId,
                body.ErrorContains,
                body.Limit,
                ct);

            return Results.Ok(new
            {
                capability = body.Capability,
                app_id = body.AppId,
                error_contains = body.ErrorContains,
                limit = body.Limit,
                requeued,
                job_ids_sample = ids.Take(20).ToList(),
            });
        });

        app.MapPost("/v1/ops/jobs/flush-backup", async (
            HttpContext ctx,
            WriteBehindPersistence persist,
            ISqliteS3Persistence sqliteS3,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();

            await persist.FlushAsync(ct);
            var backup = await sqliteS3.BackupAsync(ct);
            return Results.Ok(new
            {
                jobs_total = queue.TotalCount,
                jobs_queued = queue.QueuedCount,
                pending_writes = persist.PendingWrites,
                cache_loaded = persist.IsLoaded,
                backup_uploaded = backup.Uploaded,
                backup_skipped = backup.Skipped,
                backup_skip_reason = backup.SkipReason,
                local_job_count = backup.LocalJobCount,
                remote_job_count = backup.RemoteJobCount,
                local_bytes = backup.LocalBytes,
                remote_bytes = backup.RemoteBytes,
                dated_backup_key = backup.DatedBackupKey,
            });
        });

        app.MapGet("/v1/ops/jobs/{jobId}", (
            HttpContext ctx,
            string jobId,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(jobId))
                return Results.BadRequest(new { error = "job_id required" });

            var job = queue.Get(jobId.Trim())
                        ?? queue.SnapshotJobs()
                            .FirstOrDefault(j => j.JobId.StartsWith(jobId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (job == null)
                return Results.NotFound(new { job_id = jobId.Trim(), status = "missing" });

            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return Results.Ok(new
            {
                job_id = job.JobId,
                status = job.Status.ToString().ToLowerInvariant(),
                capability = job.Capability,
                tier = job.Tier,
                kind = job.Kind,
                model,
                worker_id = job.WorkerId,
                hostname = job.WorkerHostname,
                created_at = job.CreatedAt.ToString("O"),
                leased_until = job.LeasedUntil?.ToString("O"),
                completed_at = job.CompletedAt?.ToString("O"),
                error = job.Error,
            });
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


        app.MapPost("/v1/ops/jobs/{jobId}/cancel", async (
            HttpContext ctx,
            string jobId,
            JobService jobs,
            IOpsKeyValidator opsAuth,
            CancelJobBody? body,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var result = await jobs.CancelJobAsync(
                jobId, body?.IncludeInFlight == true, body?.DeleteArtifacts == true, ct);
            if (result == null) return Results.NotFound();
            return Results.Ok(new
            {
                job_id = result.Value.Job.JobId,
                app_id = result.Value.Job.AppId,
                status = result.Value.Job.Status,
                cancelled = result.Value.Removed,
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

        app.MapGet("/v1/ops/metrics/history", (
            HttpContext ctx,
            OpsMetricsHistory history,
            IOpsKeyValidator opsAuth,
            int limit = 60) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            return Results.Ok(new { samples = history.GetRecent(Math.Clamp(limit, 1, 120)) });
        });

        app.MapGet("/v1/ops/apps", (
            HttpContext ctx,
            ConsumerAppRegistry apps,
            InMemoryJobQueue queue,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var allJobs = queue.SnapshotJobs();
            var knownApps = allJobs.Select(j => j.AppId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var id in knownApps)
            {
                if (apps.Get(id) == null && !apps.IsPaused(id))
                    continue;
            }
            var rows = knownApps.Select(appId =>
            {
                var jobs = allJobs.Where(j => string.Equals(j.AppId, appId, StringComparison.OrdinalIgnoreCase)).ToList();
                var state = apps.Get(appId);
                return new
                {
                    app_id = appId,
                    paused = apps.IsPaused(appId),
                    pause_reason = state?.PauseReason,
                    paused_at = state?.PausedAtUtc?.ToString("O"),
                    jobs_queued = jobs.Count(j => j.Status == JobStatus.Queued),
                    jobs_in_progress = jobs.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                    jobs_failed = jobs.Count(j => j.Status == JobStatus.Failed),
                    jobs_completed = jobs.Count(j => j.Status == JobStatus.Completed),
                };
            }).OrderBy(r => r.app_id).ToList();
            return Results.Ok(new { apps = rows });
        });

        app.MapPost("/v1/ops/apps/{appId}/pause", (
            HttpContext ctx,
            string appId,
            PauseAppBody? body,
            ConsumerAppRegistry apps,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(appId))
                return Results.BadRequest(new { error = "app_id required" });
            var state = apps.Pause(appId, body?.Reason ?? "generations_exhausted", body?.PausedBy ?? "ops");
            return Results.Ok(new
            {
                app_id = state.AppId,
                paused = state.Paused,
                pause_reason = state.PauseReason,
                paused_at = state.PausedAtUtc?.ToString("O"),
            });
        });

        app.MapPost("/v1/ops/apps/{appId}/unpause", (
            HttpContext ctx,
            string appId,
            ConsumerAppRegistry apps,
            IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var state = apps.Unpause(appId);
            return state == null
                ? Results.NotFound()
                : Results.Ok(new { app_id = state.AppId, paused = false, unpaused_at = state.UnpausedAtUtc?.ToString("O") });
        });
    }

    private static void RecordMetrics(OpsMetricsHistory history, WorkerFleetTracker fleet, InMemoryJobQueue queue)
    {
        var fleetSnap = fleet.Snapshot();
        var jobs = queue.SnapshotJobs();
        var workers = fleetSnap.Workers;
        history.Record(new MetricsSample
        {
            AtUtc = DateTimeOffset.UtcNow,
            JobsQueued = jobs.Count(j => j.Status == JobStatus.Queued),
            JobsInProgress = jobs.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
            JobsFailed = jobs.Count(j => j.Status == JobStatus.Failed),
            WorkersTotal = workers.Count,
            WorkersBusy = fleetSnap.BusyCount,
            WorkersStale = fleetSnap.StaleCount,
            WorkersNonContributing = workers.Count(w => WorkerContribution.IsNonContributing(w)),
        });
    }

    internal static object BuildSnapshot(WorkerFleetTracker fleet, InMemoryJobQueue queue, ConsumerAppRegistry? apps = null)
    {
        var fleetSnap = fleet.Snapshot();
        var jobs = queue.SnapshotJobs();
        return new
        {
            generated_at = DateTimeOffset.UtcNow.ToString("O"),
            fleet = new
            {
                workers_total = fleetSnap.Workers.Count,
                workers_busy = fleetSnap.BusyCount,
                workers_idle = fleetSnap.IdleCount,
                workers_stale = fleetSnap.StaleCount,
                workers_non_contributing = fleetSnap.Workers.Count(w => WorkerContribution.IsNonContributing(w)),
                workers = fleetSnap.Workers.Select(w => EnrichWorker(w)).ToList(),
            },
            queue = BuildQueueStats(queue),
            queue_by_app = jobs
                .GroupBy(j => j.AppId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    app_id = g.Key,
                    paused = apps?.IsPaused(g.Key) ?? false,
                    queued = g.Count(j => j.Status == JobStatus.Queued),
                    in_progress = g.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                    failed = g.Count(j => j.Status == JobStatus.Failed),
                    completed = g.Count(j => j.Status == JobStatus.Completed),
                })
                .OrderByDescending(x => x.queued)
                .ToList(),
            active_jobs = queue.SnapshotJobs()
                .Where(j => j.Status is JobStatus.Leased or JobStatus.Streaming)
                .OrderBy(j => j.LeasedAt ?? j.CreatedAt)
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

    private static object EnrichWorker(WorkerSnapshot w)
    {
        var badges = WorkerContribution.Badges(w);
        return new
        {
            w.WorkerId,
            w.NodeUuid,
            w.Hostname,
            w.GpuName,
            w.VramTotalMb,
            w.VramFreeMb,
            w.DiskFreeMb,
            w.Capability,
            w.Tier,
            w.Transport,
            w.FleetMode,
            w.ComfyOk,
            w.QueueAccessOk,
            w.QueueAccessError,
            w.Busy,
            w.CurrentJobUuid,
            w.Capabilities,
            w.ClaimReadyCapabilities,
            w.KnownLoras,
            w.ModelsJson,
            w.State,
            w.ActiveJobId,
            w.JobsClaimed,
            w.JobsCompleted,
            w.JobsFailed,
            w.JobsTimedOut,
            w.JobsReleased,
            w.LastSeenAt,
            w.CheckInStale,
            contributing = badges.Count == 0,
            badges,
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
        leased_at = j.LeasedAt?.ToString("O"),
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

public sealed class ReassignConsumerRequest
{
    public string FromAppId { get; set; } = "";
    public string ToAppId { get; set; } = "";
    public string? Capability { get; set; }
    /// <summary>When set (e.g. queued), only jobs in this status are moved.</summary>
    public string? Status { get; set; } = "queued";
    /// <summary>Only JoyCaption Orchestrator assign_job payloads (excludes loboforge.com caption API jobs).</summary>
    public bool OrchestratorCaptionOnly { get; set; } = true;
}

public sealed class RetierJobsRequest
{
    [JsonPropertyName("app_id")]
    public string? AppId { get; set; }
    [JsonPropertyName("capability")]
    public string? Capability { get; set; }
    [JsonPropertyName("from_tier")]
    public string FromTier { get; set; } = "";
    [JsonPropertyName("to_tier")]
    public string ToTier { get; set; } = "";
}

public sealed class PauseAppBody
{
    public string? Reason { get; set; }
    public string? PausedBy { get; set; }
}

public sealed class CancelMatchingRequest
{
    public string? AppId { get; set; }
    public string? Capability { get; set; }
    public string? ExternalIdContains { get; set; }
    public string? PayloadContains { get; set; }
    public bool IncludeInFlight { get; set; }
}

public sealed class RequeueFailedRequest
{
    public string? Capability { get; set; }
    public string? AppId { get; set; }
    public string? ErrorContains { get; set; }
    public int? Limit { get; set; }
}
