using EventForge.Auth;
using EventForge.Core;
using EventForge.Queue;
using EventForge.Services;

namespace EventForge.Api;

public static class ConsumerEndpoints
{
    public static void MapConsumerEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/me", (HttpContext ctx, IApiKeyValidator auth, ConsumerAppRegistry apps) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var state = apps.Get(appId);
            return Results.Ok(new
            {
                app_id = appId,
                paused = apps.IsPaused(appId),
                pause_reason = state?.PauseReason,
                paused_at = state?.PausedAtUtc?.ToString("O"),
            });
        });

        app.MapGet("/v1/jobs", (
            HttpContext ctx,
            IApiKeyValidator auth,
            InMemoryJobQueue queue,
            string? status,
            string? capability,
            int limit = 100) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            limit = Math.Clamp(limit, 1, 500);
            var jobs = queue.SnapshotJobs()
                .Where(j => string.Equals(j.AppId, appId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(status))
                jobs = jobs.Where(j => string.Equals(j.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(capability))
                jobs = jobs.Where(j => string.Equals(j.Capability, capability.Trim(), StringComparison.OrdinalIgnoreCase));
            var list = jobs
                .OrderByDescending(j => j.CreatedAt)
                .Take(limit)
                .Select(ConsumerJobDto)
                .ToList();
            return Results.Ok(new { app_id = appId, count = list.Count, jobs = list });
        });

        app.MapGet("/v1/jobs/{jobId}", async (
            string jobId,
            HttpContext ctx,
            IApiKeyValidator auth,
            JobService jobs,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var job = await jobs.GetJobForAppAsync(jobId, appId, ct);
            return job == null ? Results.NotFound() : Results.Ok(ConsumerJobDto(job));
        });

        app.MapPost("/v1/jobs/cancel-queued", async (
            HttpContext ctx,
            IApiKeyValidator auth,
            JobService jobs,
            CancelQueuedBody? body,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var (removed, ids) = await jobs.CancelQueuedForAppAsync(
                appId, body?.Capability, body?.DeleteArtifacts == true, ct);
            return Results.Ok(new
            {
                app_id = appId,
                cancelled = removed,
                capability = body?.Capability,
                job_ids_sample = ids.Take(20).ToList(),
            });
        });

        app.MapPost("/v1/jobs/{jobId}/cancel", async (
            string jobId,
            HttpContext ctx,
            IApiKeyValidator auth,
            JobService jobs,
            CancelJobBody? body,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var result = await jobs.CancelJobForAppAsync(
                jobId, appId, body?.IncludeInFlight == true, body?.DeleteArtifacts == true, ct);
            if (result == null) return Results.NotFound();
            return Results.Ok(new
            {
                job_id = result.Value.Job.JobId,
                status = result.Value.Job.Status,
                cancelled = result.Value.Removed,
            });
        });

        app.MapGet("/v1/dashboard/stats", (
            HttpContext ctx,
            IApiKeyValidator auth,
            InMemoryJobQueue queue,
            ConsumerAppRegistry apps) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var jobs = queue.SnapshotJobs()
                .Where(j => string.Equals(j.AppId, appId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var now = DateTimeOffset.UtcNow;
            var last24h = jobs.Where(j => j.CreatedAt >= now.AddHours(-24)).ToList();
            var hourly = Enumerable.Range(0, 24)
                .Select(i =>
                {
                    var end = now.AddHours(-i);
                    var start = end.AddHours(-1);
                    var bucket = jobs.Where(j => j.CreatedAt >= start && j.CreatedAt < end).ToList();
                    return new
                    {
                        at_utc = end.ToString("O"),
                        jobs_queued = bucket.Count(j => j.Status == JobStatus.Queued),
                        jobs_in_progress = bucket.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                        jobs_completed = bucket.Count(j => j.Status == JobStatus.Completed),
                        jobs_failed = bucket.Count(j => j.Status == JobStatus.Failed),
                        jobs_created = bucket.Count,
                    };
                })
                .Reverse()
                .ToList();
            return Results.Ok(new
            {
                app_id = appId,
                paused = apps.IsPaused(appId),
                pause_reason = apps.Get(appId)?.PauseReason,
                jobs_total = jobs.Count,
                jobs_queued = jobs.Count(j => j.Status == JobStatus.Queued),
                jobs_in_progress = jobs.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                jobs_completed = jobs.Count(j => j.Status == JobStatus.Completed),
                jobs_failed = jobs.Count(j => j.Status == JobStatus.Failed),
                jobs_last_24h = last24h.Count,
                completed_last_24h = last24h.Count(j => j.Status == JobStatus.Completed),
                failed_last_24h = last24h.Count(j => j.Status == JobStatus.Failed),
                by_capability = jobs
                    .GroupBy(j => j.Capability, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new
                    {
                        capability = g.Key,
                        queued = g.Count(j => j.Status == JobStatus.Queued),
                        in_progress = g.Count(j => j.Status is JobStatus.Leased or JobStatus.Streaming),
                        completed = g.Count(j => j.Status == JobStatus.Completed),
                        failed = g.Count(j => j.Status == JobStatus.Failed),
                    })
                    .OrderBy(x => x.capability)
                    .ToList(),
                by_status = jobs
                    .GroupBy(j => j.Status, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new { status = g.Key, count = g.Count() })
                    .ToList(),
                recent_jobs = jobs
                    .OrderByDescending(j => j.CreatedAt)
                    .Take(25)
                    .Select(ConsumerJobDto)
                    .ToList(),
                metrics_history = hourly,
            });
        });
    }

    internal static object ConsumerJobDto(JobRecord j) => new
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
        output_url = j.OutputUrl,
        text_reply = j.TextReply,
    };
}

public sealed class CancelQueuedBody
{
    public string? Capability { get; set; }
    public bool DeleteArtifacts { get; set; }
}

public sealed class CancelJobBody
{
    [System.Text.Json.Serialization.JsonPropertyName("include_in_flight")]
    public bool IncludeInFlight { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("delete_artifacts")]
    public bool DeleteArtifacts { get; set; }
}
