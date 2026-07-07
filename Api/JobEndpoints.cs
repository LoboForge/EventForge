using System.Text.Json;
using EventForge.Auth;
using EventForge.Core;
using EventForge.Services;
using EventForge.Storage;

namespace EventForge.Api;

public static class JobEndpoints
{
    public static void MapJobEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/jobs", async (HttpContext ctx, JobService jobs, IApiKeyValidator auth, ConsumerAppRegistry apps, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();

            if (apps.IsPaused(appId))
            {
                var state = apps.Get(appId);
                return Results.Json(new
                {
                    error = "app_paused",
                    message = "Job enqueue paused — generation quota exhausted or billing hold.",
                    pause_reason = state?.PauseReason,
                    paused_at = state?.PausedAtUtc?.ToString("O"),
                }, statusCode: StatusCodes.Status402PaymentRequired);
            }

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var capability = root.TryGetProperty("capability", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(capability))
                return Results.BadRequest(new { error = "capability required" });

            var tier = root.TryGetProperty("tier", out var t) ? t.GetString() ?? "bulk" : "bulk";
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() ?? JobKind.Image : JobKind.Image;
            var payload = root.TryGetProperty("payload", out var p) ? p : root;
            string? jobId = root.TryGetProperty("job_id", out var j) ? j.GetString() : null;
            int? queuePriority = root.TryGetProperty("queue_priority", out var qp) && qp.TryGetInt32(out var qpv)
                ? qpv
                : null;

            var job = jobs.CreateJob(appId, capability, tier, kind, payload, jobId, queuePriority);
            return Results.Ok(new { job_id = job.JobId, status = job.Status, app_id = job.AppId, kind = job.Kind });
        });

        app.MapPost("/v1/jobs/claim", async (HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;

            var workerHostname = root.TryGetProperty("hostname", out var h) ? h.GetString()
                : root.TryGetProperty("worker_id", out var w) ? w.GetString() : null;

            JobRecord? job;
            if (root.TryGetProperty("capabilities", out var capsEl) && capsEl.ValueKind == JsonValueKind.Array)
            {
                var capabilities = capsEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
                if (capabilities.Count == 0)
                    return Results.BadRequest(new { error = "capabilities required" });
                job = await jobs.ClaimAnyAsync(capabilities, workerId, workerHostname, ct);
            }
            else
            {
                var capability = root.TryGetProperty("capability", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(capability))
                    return Results.BadRequest(new { error = "capability or capabilities required" });
                var tier = root.TryGetProperty("tier", out var t) ? t.GetString() ?? "*" : "*";
                job = await jobs.ClaimAsync(capability, tier, workerId, workerHostname, ct);
            }
            if (job == null) return Results.NoContent();
            return Results.Ok(new
            {
                job_id = job.JobId,
                app_id = job.AppId,
                capability = job.Capability,
                tier = job.Tier,
                kind = job.Kind,
                worker_id = job.WorkerId,
                hostname = job.WorkerHostname,
                leased_until = job.LeasedUntil?.ToString("O"),
                payload = JsonDocument.Parse(job.PayloadJson).RootElement,
            });
        });


        app.MapPut("/v1/jobs/{jobId}/input", async (
            string jobId, HttpContext ctx, JobService jobs, IApiKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out _))
                return Results.Unauthorized();

            var fileName = ctx.Request.Query["file"].ToString();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "input.bin";
            var contentType = ctx.Request.ContentType ?? "application/octet-stream";
            try
            {
                var ok = await jobs.SaveInputStreamAsync(jobId, fileName, contentType, ctx.Request.Body, ct);
                return ok ? Results.Ok(new { ok = true, job_id = jobId, file = Path.GetFileName(fileName) }) : Results.BadRequest();
            }
            catch (UploadSaturationException ex)
            {
                ctx.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString();
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapGet("/v1/jobs/{jobId}/input/{fileName}", async (
            string jobId, string fileName, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out _))
                return Results.Unauthorized();

            var opened = await jobs.OpenInputStreamAsync(jobId, fileName, ct);
            if (opened == null) return Results.NotFound();
            var (stream, contentType) = opened.Value;
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.Stream(stream, contentType);
        });

        app.MapPut("/v1/jobs/{jobId}/output", async (
            string jobId, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            var fileName = ctx.Request.Query["file"].ToString();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "output.png";
            var contentType = ctx.Request.ContentType ?? "application/octet-stream";
            try
            {
                var ok = await jobs.SaveOutputStreamAsync(jobId, workerId, fileName, contentType, ctx.Request.Body, ct);
                return ok ? Results.Ok(new { ok = true, job_id = jobId }) : Results.NotFound();
            }
            catch (UploadSaturationException ex)
            {
                ctx.Response.Headers.RetryAfter = ex.RetryAfterSeconds.ToString();
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/v1/jobs/{jobId}/stream", async (
            string jobId, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var delta = doc.RootElement.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
            if (delta.Length == 0) return Results.BadRequest(new { error = "delta required" });
            var ok = await jobs.PushStreamTokenAsync(jobId, workerId, delta, ct);
            return ok ? Results.Ok(new { ok = true }) : Results.NotFound();
        });

        app.MapPost("/v1/jobs/{jobId}/complete", async (
            string jobId, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            string? text = null;
            if (ctx.Request.ContentLength > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text = t.GetString();
            }

            var job = await jobs.CompleteAsync(jobId, workerId, text, ct);
            return job == null ? Results.NotFound() : Results.Ok(new { job_id = job.JobId, status = job.Status });
        });


        app.MapPost("/v1/jobs/{jobId}/release", async (
            string jobId, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();
            var ok = await jobs.ReleaseAsync(jobId, workerId, ct);
            return ok ? Results.Ok(new { ok = true, job_id = jobId, status = "queued" }) : Results.NotFound();
        });

        app.MapPost("/v1/jobs/{jobId}/fail", async (
            string jobId, HttpContext ctx, JobService jobs, IWorkerKeyValidator auth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "failed" : "failed";
            var job = await jobs.FailAsync(jobId, workerId, error, ct);
            return job == null ? Results.NotFound() : Results.Ok(new { job_id = job.JobId, status = job.Status });
        });
    }
}
