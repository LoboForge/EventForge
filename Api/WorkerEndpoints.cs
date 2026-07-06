using System.Text.Json;
using EventForge.Auth;
using EventForge.Queue;
using EventForge.Services;
using EventForge.WebSocket;

namespace EventForge.Api;

public static class WorkerEndpoints
{
    public static void MapWorkerEndpoints(this WebApplication app)
    {
        app.MapPost("/v1/workers/check-in", async (
            HttpContext ctx,
            WorkerFleetTracker fleet,
            JobService jobs,
            InMemoryJobQueue queue,
            OpsEventHub ops,
            IWorkerKeyValidator auth,
            CancellationToken ct) =>
        {
            if (!AuthHelpers.TryReadApiKey(ctx, out var token) || !auth.TryValidate(token, out var workerId))
                return Results.Unauthorized();

            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            var root = doc.RootElement;
            var payload = ParseCheckIn(root);
            fleet.RegisterCheckIn(workerId, payload);
            var leaseExtended = payload.Busy && !string.IsNullOrWhiteSpace(payload.CurrentJobUuid)
                && await jobs.ExtendLeaseOnCheckInAsync(workerId, payload.CurrentJobUuid, ct);
            await ops.PublishFleetSnapshotAsync(new
            {
                type = "ops.fleet.snapshot",
                snapshot = OpsEndpoints.BuildSnapshot(fleet, queue),
            }, ct);

            return Results.Ok(new
            {
                ok = true,
                worker_id = workerId,
                acknowledged_models = payload.KnownLoras ?? Array.Empty<string>(),
                lease_extended = leaseExtended,
            });
        });
    }

    internal static WorkerCheckInPayload ParseCheckIn(JsonElement root)
    {
        var caps = new List<string>();
        if (root.TryGetProperty("forge_queue_capabilities", out var capsEl) && capsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in capsEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    caps.Add(item.GetString()!.Trim());
        }

        var loras = new List<string>();
        if (root.TryGetProperty("known_loras", out var lorasEl) && lorasEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in lorasEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    loras.Add(item.GetString()!.Trim());
        }

        var claimReady = new List<string>();
        if (root.TryGetProperty("claim_ready_capabilities", out var readyEl) && readyEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in readyEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    claimReady.Add(item.GetString()!.Trim());
        }

        string? modelsJson = null;
        if (root.TryGetProperty("models", out var modelsEl))
            modelsJson = modelsEl.GetRawText();

        return new WorkerCheckInPayload
        {
            NodeUuid = ReadString(root, "node_uuid"),
            Hostname = ReadString(root, "hostname"),
            GpuName = ReadString(root, "gpu_name"),
            VramTotal = ReadLong(root, "vram_total"),
            VramFree = ReadLong(root, "vram_free"),
            DiskFreeMb = ReadLong(root, "disk_free_mb"),
            Transport = ReadString(root, "transport") ?? "eventforge",
            FleetMode = ReadString(root, "fleet_mode", "provision_mode"),
            ComfyOk = !root.TryGetProperty("comfy_ok", out var comfy) || comfy.ValueKind != JsonValueKind.False,
            QueueAccessOk = root.TryGetProperty("queue_access_ok", out var qa) && qa.ValueKind == JsonValueKind.True
                ? true
                : root.TryGetProperty("queue_access_ok", out var qf) && qf.ValueKind == JsonValueKind.False
                    ? false
                    : null,
            QueueAccessError = ReadString(root, "queue_access_error"),
            Busy = root.TryGetProperty("busy", out var busy) && busy.ValueKind == JsonValueKind.True,
            CurrentJobUuid = ReadString(root, "current_job_uuid"),
            ModelsJson = modelsJson,
            KnownLoras = loras,
            ForgeQueueCapabilities = caps,
            ClaimReadyCapabilities = claimReady,
        };
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static long ReadLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), out var parsed) => parsed,
            _ => 0,
        };
    }
}
