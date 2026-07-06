using System.Collections.Concurrent;
using EventForge.Auth;
using EventForge.Configuration;
using EventForge.Infrastructure;
using EventForge.VastAi;
using Microsoft.Extensions.Options;

namespace EventForge.Api;

public static class VastEndpoints
{
    private static readonly ConcurrentDictionary<long, (DateTime ExpiresUtc, string Reason, string Detail)> Blacklist = new();

    public static void MapVastEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/ops/vast/status", (HttpContext ctx, IVastAiClient vast, IOpsKeyValidator opsAuth, IOptions<EventForgeOptions> opts) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            return Results.Ok(new
            {
                configured = vast.IsConfigured,
                event_forge_url = opts.Value.PublicUrl,
            });
        });

        app.MapGet("/v1/ops/vast/account", async (HttpContext ctx, IVastAiClient vast, IOpsKeyValidator opsAuth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (!vast.IsConfigured) return Results.StatusCode(503);
            var a = await vast.GetAccountAsync(ct);
            return a == null ? Results.StatusCode(502) : Results.Ok(VastAiDtoMapper.ToDto(a));
        });

        app.MapPost("/v1/ops/vast/search", async (HttpContext ctx, IVastAiClient vast, IOpsKeyValidator opsAuth, SearchQuery q, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var offers = await SearchFilteredAsync(vast, q, ct);
            return Results.Ok(offers);
        });

        app.MapGet("/v1/ops/vast/recommend", async (
            HttpContext ctx,
            IVastAiClient vast,
            IOpsKeyValidator opsAuth,
            string mode,
            bool loosen,
            string? exclude,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var minVram = mode switch { "video" or "all" or "ltx-native" => 24, _ => 16 };
            var q = new SearchQuery
            {
                MinGpuRamGb = minVram,
                MaxDollarsPerHr = loosen ? 0.50m : 0.15m,
                MinReliability = loosen ? 0.85m : 0.95m,
                VerifiedOnly = true,
                SortBy = "bang",
                Limit = 10,
            };
            var excludeIds = ParseExcludeOfferIds(exclude);
            var offers = await SearchFilteredAsync(vast, q, ct, VastAiDiskRequirements.MinimumHostDiskGb(mode));
            if (excludeIds.Count > 0)
                offers = offers.Where(o => !excludeIds.Contains(o.Id)).ToList();
            return Results.Ok(new { mode, offer = offers.FirstOrDefault() });
        });

        app.MapGet("/v1/ops/vast/provision-command", (HttpContext ctx, IOpsKeyValidator opsAuth, IOptions<EventForgeOptions> opts, string instanceId, string mode) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var ef = opts.Value;
            var secret = WorkerBootstrapDefaults.ResolveWorkerSecret(ef);
            var hf = WorkerBootstrapDefaults.ResolveHfToken(ef);
            var id = string.IsNullOrWhiteSpace(instanceId) ? "(set instance id)" : instanceId.Trim();
            return Results.Ok(new
            {
                command = WorkerBootstrapDefaults.BuildManualProvisionCommand(
                    ef, id, mode, secret, "wss://www.loboforge.com", ef.PublicUrl, hf),
                hasSecret = !string.IsNullOrWhiteSpace(secret),
                hasHfToken = !string.IsNullOrWhiteSpace(hf),
                extraEnvTemplate = WorkerBootstrapDefaults.BuildEventForgeExtraEnvTemplate(ef, mode),
            });
        });

        app.MapGet("/v1/ops/vast/instances/live", async (HttpContext ctx, IVastAiClient vast, IOpsKeyValidator opsAuth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (!vast.IsConfigured) return Results.StatusCode(503);
            var list = await vast.GetMyInstancesAsync(ct);
            return Results.Ok(list.Select(VastAiDtoMapper.ToDto));
        });

        app.MapPost("/v1/ops/vast/rent", async (HttpContext ctx, IVastAiClient vast, IOpsKeyValidator opsAuth, IOptions<EventForgeOptions> opts, RentBody body, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (!vast.IsConfigured) return Results.StatusCode(503);
            if (body.OfferId <= 0) return Results.BadRequest(new { error = "OfferId required." });
            var ef = opts.Value;
            var p = new CreateInstanceParams
            {
                OfferId = body.OfferId,
                Mode = body.Mode,
                HfToken = WorkerBootstrapDefaults.ResolveHfToken(ef, body.HfToken),
                LoboSecret = WorkerBootstrapDefaults.ResolveWorkerSecret(ef, body.LoboSecret),
                LoboServer = "wss://www.loboforge.com",
                LoboBaseUrl = ef.AgentScriptBaseUrl,
                DiskGb = VastAiDiskRequirements.ClampRentDiskGb(body.Mode, body.DiskGb),
                Label = body.Label,
                DockerImage = body.DockerImage ?? "vastai/comfy:v0.15.1-cuda-12.9-py312",
            };
            var result = await vast.CreateInstanceAsync(p, ct);
            return result.Ok
                ? Results.Ok(new { ok = true, instanceId = result.InstanceId })
                : Results.BadRequest(new { error = result.Error, raw = result.RawResponse });
        });

        app.MapPost("/v1/ops/vast/terminate/{instanceId:long}", async (HttpContext ctx, long instanceId, IVastAiClient vast, IOpsKeyValidator opsAuth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var (ok, err) = await vast.DestroyInstanceAsync(instanceId, ct);
            return ok ? Results.Ok() : Results.BadRequest(new { error = err });
        });

        app.MapPost("/v1/ops/vast/stop/{instanceId:long}", async (HttpContext ctx, long instanceId, IVastAiClient vast, IOpsKeyValidator opsAuth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var (ok, err) = await vast.StopInstanceAsync(instanceId, ct);
            return ok ? Results.Ok() : Results.BadRequest(new { error = err });
        });

        app.MapPost("/v1/ops/vast/start/{instanceId:long}", async (HttpContext ctx, long instanceId, IVastAiClient vast, IOpsKeyValidator opsAuth, CancellationToken ct) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var (ok, err) = await vast.StartInstanceAsync(instanceId, ct);
            return ok ? Results.Ok() : Results.BadRequest(new { error = err });
        });

        app.MapGet("/v1/ops/vast/blacklist", (HttpContext ctx, IOpsKeyValidator opsAuth) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            var now = DateTime.UtcNow;
            var rows = Blacklist
                .Where(kv => kv.Value.ExpiresUtc > now)
                .Select(kv => new { machineId = kv.Key, reason = kv.Value.Reason, detail = kv.Value.Detail, expiresUtc = kv.Value.ExpiresUtc })
                .ToList();
            return Results.Ok(rows);
        });

        app.MapPost("/v1/ops/vast/blacklist", (HttpContext ctx, IOpsKeyValidator opsAuth, BlacklistBody body) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            if (body.MachineId <= 0) return Results.BadRequest(new { error = "MachineId required." });
            Blacklist[body.MachineId] = (DateTime.UtcNow.AddHours(24), body.Reason ?? "manual", body.Detail ?? "");
            return Results.Ok(new { machineId = body.MachineId });
        });

        app.MapDelete("/v1/ops/vast/blacklist/{machineId:long}", (HttpContext ctx, IOpsKeyValidator opsAuth, long machineId) =>
        {
            if (!AuthHelpers.TryAuthorizeOps(ctx, opsAuth, out _))
                return Results.Unauthorized();
            Blacklist.TryRemove(machineId, out _);
            return Results.Ok();
        });
    }

    public class SearchQuery
    {
        public int MinGpuRamGb { get; set; } = 16;
        public decimal MaxDollarsPerHr { get; set; } = 0.75m;
        public decimal MinReliability { get; set; } = 0.95m;
        public bool VerifiedOnly { get; set; } = true;
        public string GpuNameContains { get; set; } = "";
        public string SortBy { get; set; } = "best";
        public int Limit { get; set; } = 50;
    }

    public class RentBody
    {
        public long OfferId { get; set; }
        public string Mode { get; set; } = "image";
        public int DiskGb { get; set; }
        public string Label { get; set; } = "";
        public string? HfToken { get; set; }
        public string? LoboSecret { get; set; }
        public string? DockerImage { get; set; }
    }

    public record BlacklistBody(long MachineId, string? Reason, string? Detail);

    private static async Task<List<VastOfferDto>> SearchFilteredAsync(
        IVastAiClient vast,
        SearchQuery q,
        CancellationToken ct,
        int? minHostDiskGb = null)
    {
        var filter = new VastOfferFilter
        {
            MinGpuRamGb = q.MinGpuRamGb,
            MaxDollarsPerHr = q.MaxDollarsPerHr,
            MinReliability = q.MinReliability,
            VerifiedOnly = q.VerifiedOnly,
            GpuNameContains = q.GpuNameContains ?? "",
            SortBy = q.SortBy ?? "best",
            Limit = q.Limit > 0 ? q.Limit : 50,
            MinHostDiskGb = minHostDiskGb ?? 70,
        };
        var offers = await vast.SearchOffersAsync(filter, ct);
        var blocked = GetBlacklistedMachineIds();
        return offers
            .Where(o => !blocked.Contains(o.MachineId))
            .Select(VastAiDtoMapper.ToDto)
            .ToList();
    }

    private static HashSet<long> GetBlacklistedMachineIds()
    {
        var now = DateTime.UtcNow;
        return Blacklist.Where(kv => kv.Value.ExpiresUtc > now).Select(kv => kv.Key).ToHashSet();
    }

    private static HashSet<long> ParseExcludeOfferIds(string? exclude)
    {
        var ids = new HashSet<long>();
        if (string.IsNullOrWhiteSpace(exclude)) return ids;
        foreach (var part in exclude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part, out var id) && id > 0) ids.Add(id);
        return ids;
    }
}
