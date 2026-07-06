namespace EventForge.Services;

using System.Collections.Concurrent;

/// <summary>In-memory fleet view for ops dashboard (check-in + claim lifecycle).</summary>
public sealed class WorkerFleetTracker
{
    private readonly ConcurrentDictionary<string, WorkerStats> _workers = new(StringComparer.OrdinalIgnoreCase);
    public static readonly TimeSpan CheckInStaleAfter = TimeSpan.FromSeconds(90);

    public void RegisterCheckIn(string workerKey, WorkerCheckInPayload payload)
    {
        var fleetKey = ResolveFleetKey(workerKey, payload.NodeUuid, payload.Hostname);
        var stats = GetOrAdd(fleetKey, payload.Hostname, workerKey);
        stats.NodeUuid = payload.NodeUuid ?? workerKey;
        stats.Hostname = NormalizeHostname(payload.Hostname, workerKey);
        stats.GpuName = payload.GpuName ?? "";
        stats.VramTotalMb = payload.VramTotal;
        stats.VramFreeMb = payload.VramFree;
        stats.DiskFreeMb = payload.DiskFreeMb;
        stats.Transport = payload.Transport ?? "eventforge";
        stats.FleetMode = payload.FleetMode ?? "";
        stats.ComfyOk = payload.ComfyOk;
        stats.QueueAccessOk = payload.QueueAccessOk;
        stats.QueueAccessError = payload.QueueAccessError;
        stats.Busy = payload.Busy;
        stats.CurrentJobUuid = payload.CurrentJobUuid;
        stats.ModelsJson = payload.ModelsJson;
        stats.KnownLoras = payload.KnownLoras?.ToList() ?? [];
        stats.Capabilities = payload.ForgeQueueCapabilities?.ToList() ?? [];
        stats.ClaimReadyCapabilities = payload.ClaimReadyCapabilities?.ToList() ?? [];
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        if (payload.Busy && !string.IsNullOrWhiteSpace(payload.CurrentJobUuid))
        {
            stats.ActiveJobId = payload.CurrentJobUuid;
            stats.State = "busy";
        }
        else if (stats.State != "busy" || string.IsNullOrWhiteSpace(stats.ActiveJobId))
        {
            stats.State = payload.Busy ? "busy" : "idle";
        }
    }

    public void OnClaim(string workerKey, string? hostname, string capability, string tier, string jobId)
    {
        var fleetKey = ResolveLifecycleFleetKey(workerKey, null, hostname);
        var stats = GetOrAdd(fleetKey, hostname, workerKey);
        Interlocked.Increment(ref stats.JobsClaimed);
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        stats.Capability = capability;
        stats.Tier = tier;
        stats.ActiveJobId = jobId;
        stats.CurrentJobUuid = jobId;
        stats.State = "busy";
        stats.Busy = true;
    }

    public void OnComplete(string? workerKey, string? hostname)
    {
        if (string.IsNullOrWhiteSpace(workerKey)) return;
        var fleetKey = ResolveLifecycleFleetKey(workerKey, null, hostname);
        var stats = GetOrAdd(fleetKey, hostname, workerKey);
        Interlocked.Increment(ref stats.JobsCompleted);
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        stats.ActiveJobId = null;
        stats.CurrentJobUuid = null;
        stats.State = "idle";
        stats.Busy = false;
    }

    public void OnFail(string? workerKey, string? hostname)
    {
        if (string.IsNullOrWhiteSpace(workerKey)) return;
        var fleetKey = ResolveLifecycleFleetKey(workerKey, null, hostname);
        var stats = GetOrAdd(fleetKey, hostname, workerKey);
        Interlocked.Increment(ref stats.JobsFailed);
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        stats.ActiveJobId = null;
        stats.CurrentJobUuid = null;
        stats.State = "idle";
        stats.Busy = false;
    }

    public void OnTimeout(string? workerKey, string? hostname)
    {
        if (string.IsNullOrWhiteSpace(workerKey)) return;
        var fleetKey = ResolveLifecycleFleetKey(workerKey, null, hostname);
        var stats = GetOrAdd(fleetKey, hostname, workerKey);
        Interlocked.Increment(ref stats.JobsTimedOut);
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        stats.ActiveJobId = null;
        stats.CurrentJobUuid = null;
        stats.State = "idle";
        stats.Busy = false;
    }

    public void OnRelease(string? workerKey, string? hostname)
    {
        if (string.IsNullOrWhiteSpace(workerKey)) return;
        var fleetKey = ResolveLifecycleFleetKey(workerKey, null, hostname);
        var stats = GetOrAdd(fleetKey, hostname, workerKey);
        Interlocked.Increment(ref stats.JobsReleased);
        stats.LastSeenUtc = DateTimeOffset.UtcNow;
        stats.ActiveJobId = null;
        stats.CurrentJobUuid = null;
        stats.State = "idle";
        stats.Busy = false;
    }

    public WorkerSnapshot? TryGetWorker(string workerKey)
    {
        if (_workers.TryGetValue(workerKey, out var stats)) return ToSnapshot(stats);
        foreach (var entry in _workers.Values)
        {
            if (string.Equals(entry.WorkerKey, workerKey, StringComparison.OrdinalIgnoreCase))
                return ToSnapshot(entry);
        }
        return null;
    }

    public WorkerSnapshot? TryGetWorkerBusyOnJob(string authWorkerId, string jobId)
    {
        foreach (var stats in _workers.Values)
        {
            if (!string.Equals(stats.WorkerKey, authWorkerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(stats.CurrentJobUuid, jobId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!stats.Busy) continue;
            return ToSnapshot(stats);
        }
        return null;
    }

    public WorkerSnapshot? TryGetWorkerByHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        var host = hostname.Trim();
        foreach (var stats in _workers.Values)
        {
            if (string.Equals(stats.Hostname, host, StringComparison.OrdinalIgnoreCase))
                return ToSnapshot(stats);
        }
        return null;
    }

    public FleetSnapshot Snapshot()
    {
        var workers = SnapshotWorkers();
        return new FleetSnapshot
        {
            Workers = workers,
            BusyCount = workers.Count(w => w.State == "busy"),
            IdleCount = workers.Count(w => w.State == "idle"),
            StaleCount = workers.Count(w => w.CheckInStale),
        };
    }

    public IReadOnlyList<WorkerSnapshot> SnapshotWorkers()
    {
        var cutoff = DateTimeOffset.UtcNow - CheckInStaleAfter;
        return _workers.Values
            .OrderByDescending(w => w.LastSeenUtc)
            .Select(ToSnapshot)
            .ToList();
    }

    private static WorkerSnapshot ToSnapshot(WorkerStats w)
    {
        var cutoff = DateTimeOffset.UtcNow - CheckInStaleAfter;
        return new WorkerSnapshot
        {
            WorkerId = w.WorkerKey,
            NodeUuid = w.NodeUuid,
            Hostname = w.Hostname,
            GpuName = w.GpuName,
            VramTotalMb = w.VramTotalMb,
            VramFreeMb = w.VramFreeMb,
            DiskFreeMb = w.DiskFreeMb,
            Capability = w.Capability,
            Tier = w.Tier,
            Transport = w.Transport,
            FleetMode = w.FleetMode,
            ComfyOk = w.ComfyOk,
            QueueAccessOk = w.QueueAccessOk,
            QueueAccessError = w.QueueAccessError,
            Busy = w.Busy,
            CurrentJobUuid = w.CurrentJobUuid,
            Capabilities = w.Capabilities,
            ClaimReadyCapabilities = w.ClaimReadyCapabilities,
            KnownLoras = w.KnownLoras,
            ModelsJson = w.ModelsJson,
            State = w.State,
            ActiveJobId = w.ActiveJobId,
            JobsClaimed = w.JobsClaimed,
            JobsCompleted = w.JobsCompleted,
            JobsFailed = w.JobsFailed,
            JobsTimedOut = w.JobsTimedOut,
            JobsReleased = w.JobsReleased,
            LastSeenAt = w.LastSeenUtc.ToString("O"),
            CheckInStale = w.LastSeenUtc < cutoff,
        };
    }

    private WorkerStats GetOrAdd(string fleetKey, string? hostname, string authWorkerId)
    {
        return _workers.AddOrUpdate(
            fleetKey,
            _ => new WorkerStats
            {
                WorkerKey = authWorkerId,
                NodeUuid = fleetKey,
                Hostname = NormalizeHostname(hostname, fleetKey),
            },
            (_, existing) =>
            {
                var host = NormalizeHostname(hostname, fleetKey);
                if (!string.IsNullOrWhiteSpace(host))
                    existing.Hostname = host;
                return existing;
            });
    }

    /// <summary>
    /// Fleet rows are keyed per box (node_uuid or hostname). Auth worker id is shared across Vast boxes.
    /// </summary>
    internal static string ResolveFleetKey(string workerKey, string? nodeUuid, string? hostname)
    {
        if (!string.IsNullOrWhiteSpace(nodeUuid)) return nodeUuid.Trim();
        if (!string.IsNullOrWhiteSpace(hostname)) return hostname.Trim();
        return workerKey;
    }

    /// <summary>
    /// Job lifecycle events only carry hostname — reuse the check-in row keyed by node_uuid when possible.
    /// </summary>
    internal string ResolveLifecycleFleetKey(string workerKey, string? nodeUuid, string? hostname)
    {
        if (!string.IsNullOrWhiteSpace(hostname))
        {
            var host = hostname.Trim();
            foreach (var (key, stats) in _workers)
            {
                if (string.Equals(stats.Hostname, host, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
        }

        return ResolveFleetKey(workerKey, nodeUuid, hostname);
    }

    private static string NormalizeHostname(string? hostname, string fallback) =>
        string.IsNullOrWhiteSpace(hostname) ? fallback : hostname.Trim();

    private sealed class WorkerStats
    {
        public required string WorkerKey { get; init; }
        public string NodeUuid { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string GpuName { get; set; } = "";
        public long VramTotalMb { get; set; }
        public long VramFreeMb { get; set; }
        public long DiskFreeMb { get; set; }
        public string Capability { get; set; } = "";
        public string Tier { get; set; } = "";
        public string Transport { get; set; } = "eventforge";
        public string FleetMode { get; set; } = "";
        public bool ComfyOk { get; set; } = true;
        public bool? QueueAccessOk { get; set; }
        public string? QueueAccessError { get; set; }
        public bool Busy { get; set; }
        public string? CurrentJobUuid { get; set; }
        public List<string> Capabilities { get; set; } = [];
        public List<string> ClaimReadyCapabilities { get; set; } = [];
        public List<string> KnownLoras { get; set; } = [];
        public string? ModelsJson { get; set; }
        public string State { get; set; } = "idle";
        public string? ActiveJobId { get; set; }
        public int JobsClaimed;
        public int JobsCompleted;
        public int JobsFailed;
        public int JobsTimedOut;
        public int JobsReleased;
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    }
}

public sealed class WorkerCheckInPayload
{
    public string? NodeUuid { get; init; }
    public string? Hostname { get; init; }
    public string? GpuName { get; init; }
    public long VramTotal { get; init; }
    public long VramFree { get; init; }
    public long DiskFreeMb { get; init; }
    public string? Transport { get; init; }
    public string? FleetMode { get; init; }
    public bool ComfyOk { get; init; } = true;
    public bool? QueueAccessOk { get; init; }
    public string? QueueAccessError { get; init; }
    public bool Busy { get; init; }
    public string? CurrentJobUuid { get; init; }
    public string? ModelsJson { get; init; }
    public IReadOnlyList<string>? KnownLoras { get; init; }
    public IReadOnlyList<string>? ForgeQueueCapabilities { get; init; }
    public IReadOnlyList<string>? ClaimReadyCapabilities { get; init; }
}

public sealed class FleetSnapshot
{
    public required IReadOnlyList<WorkerSnapshot> Workers { get; init; }
    public int BusyCount { get; init; }
    public int IdleCount { get; init; }
    public int StaleCount { get; init; }
}

public sealed class WorkerSnapshot
{
    public string WorkerId { get; init; } = "";
    public string NodeUuid { get; init; } = "";
    public string Hostname { get; init; } = "";
    public string GpuName { get; init; } = "";
    public long VramTotalMb { get; init; }
    public long VramFreeMb { get; init; }
    public long DiskFreeMb { get; init; }
    public string Capability { get; init; } = "";
    public string Tier { get; init; } = "";
    public string Transport { get; init; } = "";
    public string FleetMode { get; init; } = "";
    public bool ComfyOk { get; init; } = true;
    public bool? QueueAccessOk { get; init; }
    public string? QueueAccessError { get; init; }
    public bool Busy { get; init; }
    public string? CurrentJobUuid { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> ClaimReadyCapabilities { get; init; } = [];
    public IReadOnlyList<string> KnownLoras { get; init; } = [];
    public string? ModelsJson { get; init; }
    public string State { get; init; } = "idle";
    public string? ActiveJobId { get; init; }
    public int JobsClaimed { get; init; }
    public int JobsCompleted { get; init; }
    public int JobsFailed { get; init; }
    public int JobsTimedOut { get; init; }
    public int JobsReleased { get; init; }
    public string LastSeenAt { get; init; } = "";
    public bool CheckInStale { get; init; }
}
