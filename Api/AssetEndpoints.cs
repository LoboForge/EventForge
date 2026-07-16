using System.Text.Json;
using EventForge.Auth;
using EventForge.Models;
using EventForge.Services;
using Microsoft.AspNetCore.Http.Features;

namespace EventForge.Api;

public static class AssetEndpoints
{
    public static void MapAssetEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/assets/loras", async (
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var fileName = root.TryGetProperty("file_name", out var fn) ? fn.GetString()
                : root.TryGetProperty("fileName", out var fn2) ? fn2.GetString() : null;
            var modes = root.TryGetProperty("modes", out var m) ? m.GetString() : null;
            long? bytes = null;
            if (root.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var bv))
                bytes = bv;
            var sha256 = root.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            var replace = root.TryGetProperty("replace", out var r) && r.ValueKind == JsonValueKind.True;

            try
            {
                var started = await loras.BeginUploadAsync(appId, fileName ?? "", modes, bytes, sha256, replace, ct);
                if (started == null) return Results.BadRequest(new { error = "begin_failed" });
                var (asset, method, url, headers) = started.Value;
                return Results.Ok(new
                {
                    asset_id = asset.AssetId,
                    app_id = asset.AppId,
                    file_name = asset.FileName,
                    modes = asset.Modes,
                    status = asset.Status,
                    upload = new
                    {
                        method,
                        url,
                        headers,
                    },
                });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex) when (ex.Message == "asset_exists")
            {
                return Results.Conflict(new { error = "asset_exists", message = "LoRA already registered; pass replace=true or DELETE first" });
            }
        });

        app.MapPut("/v1/assets/loras/{assetId}/content", async (
            string assetId,
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();

            // This endpoint consumes Request.Body directly; do not parse forms or buffer the upload.
            // Raise Kestrel's per-request cap before the body is read so local/proxy uploads can use
            // the same configured limit as direct S3 uploads.
            var sizeFeature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = loras.MaxUploadBytes;

            if (ctx.Request.ContentLength > loras.MaxUploadBytes)
                return Results.BadRequest(new { error = "file_too_large" });

            try
            {
                var record = await loras.SaveContentAsync(
                    appId, assetId, ctx.Request.Body, ctx.Request.ContentType, ct);
                if (record == null) return Results.NotFound();
                return Results.Ok(new
                {
                    asset_id = record.AssetId,
                    file_name = record.FileName,
                    bytes = record.Bytes,
                    status = record.Status,
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/v1/assets/loras/{assetId}/complete", async (
            string assetId,
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();

            long? bytes = null;
            string? sha256 = null;
            if (ctx.Request.ContentLength is > 0)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                var root = doc.RootElement;
                if (root.TryGetProperty("bytes", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt64(out var bv))
                    bytes = bv;
                sha256 = root.TryGetProperty("sha256", out var s) ? s.GetString() : null;
            }

            try
            {
                var record = await loras.CompleteAsync(appId, assetId, bytes, sha256, ct);
                if (record == null) return Results.NotFound();
                return Results.Ok(ToDto(record));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/v1/assets/loras", (
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras,
            string? status) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var list = loras.List(appId, status).Select(ToDto).ToList();
            return Results.Ok(new { app_id = appId, count = list.Count, loras = list });
        });

        app.MapGet("/v1/assets/loras/{assetId}", (
            string assetId,
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var record = loras.GetForApp(appId, assetId);
            return record == null ? Results.NotFound() : Results.Ok(ToDto(record));
        });

        app.MapDelete("/v1/assets/loras/{assetId}", async (
            string assetId,
            HttpContext ctx,
            IApiKeyValidator auth,
            LoraAssetService loras,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var appId))
                return Results.Unauthorized();
            var ok = await loras.DeleteForAppAsync(appId, assetId, ct);
            return ok ? Results.Ok(new { ok = true, asset_id = assetId }) : Results.NotFound();
        });

        // Worker: download a ready LoRA referenced by a job (app-scoped via job.AppId).
        app.MapGet("/v1/jobs/{jobId}/loras/{fileName}", async (
            string jobId,
            string fileName,
            HttpContext ctx,
            IWorkerKeyValidator auth,
            LoraAssetService loras,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            var opened = await loras.TryOpenForJobAsync(jobId, fileName, workerId, ct);
            if (opened == null) return Results.NotFound();
            var (stream, contentType, length, name) = opened.Value;
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            ctx.Response.Headers.ContentLength = length;
            ctx.Response.Headers["X-Lora-File-Name"] = name;
            return Results.Stream(stream, contentType, fileDownloadName: name);
        });
    }

    private static object ToDto(LoraAssetRecord r) => new
    {
        asset_id = r.AssetId,
        app_id = r.AppId,
        file_name = r.FileName,
        modes = r.Modes,
        status = r.Status,
        bytes = r.Bytes,
        sha256 = r.Sha256,
        created_at = r.CreatedAt.ToString("O"),
        completed_at = r.CompletedAt?.ToString("O"),
    };
}
