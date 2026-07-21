using System.Text;
using System.Text.Json;
using EventForge.Configuration;
using EventForge.Core;
using EventForge.Infrastructure;
using EventForge.Models;
using EventForge.Persistence;
using EventForge.Queue;
using EventForge.Storage;
using EventForge.WebSocket;
using Microsoft.Extensions.Options;

namespace EventForge.Services;

public sealed record ClaimDiagnostics(
    int QueuedMatching,
    int BlockedPaused,
    int BlockedModel,
    int BlockedLora,
    IReadOnlyList<string> MissingLoras);

/// <summary>Result of an ops moderation delete of a finished job.</summary>
/// <param name="Found">Whether a job record existed.</param>
/// <param name="Status">Status the job had when found.</param>
/// <param name="AppId">Owning consumer app id, when known.</param>
/// <param name="Deleted">Whether the job record was removed.</param>
/// <param name="EventsDeleted">Number of persisted event rows removed.</param>
/// <param name="ActiveConflict">True when the job was still queued/in-flight and was NOT deleted.</param>
public sealed record JobDeletionResult(
    bool Found,
    string? Status,
    string? AppId,
    bool Deleted,
    int EventsDeleted,
    bool ActiveConflict);

public sealed class JobService
{
    /// <summary>Sticky error markers written when a job is cancelled. A late worker
    /// <c>/complete</c> or <c>/output</c> for one of these must not resurrect it.</summary>
    public const string CancelledByOps = "cancelled_by_ops";
    public const string CancelledByConsumer = "cancelled_by_consumer";

    internal static bool IsCancelledTerminal(JobRecord job) =>
        string.Equals(job.Status, JobStatus.Failed, StringComparison.OrdinalIgnoreCase)
        && (string.Equals(job.Error, CancelledByOps, StringComparison.OrdinalIgnoreCase)
            || string.Equals(job.Error, CancelledByConsumer, StringComparison.OrdinalIgnoreCase));

    private readonly EventForgeOptions _opts;
    private readonly InMemoryJobQueue _queue;
    private readonly IEventStore _events;
    private readonly IArtifactStore _artifacts;
    private readonly WsConnectionManager _ws;
    private readonly WriteBehindPersistence _persist;
    private readonly WorkerFleetTracker _fleet;
    private readonly OpsEventHub _ops;
    private readonly ConsumerAppRegistry _apps;
    private readonly LoraAssetService _loras;
    private readonly ILogger<JobService> _log;
    private readonly Dictionary<string, StringBuilder> _streamBuffers = new(StringComparer.OrdinalIgnoreCase);

    public JobService(
        IOptions<EventForgeOptions> options,
        InMemoryJobQueue queue,
        IEventStore events,
        IArtifactStore artifacts,
        WsConnectionManager ws,
        WriteBehindPersistence persist,
        WorkerFleetTracker fleet,
        OpsEventHub ops,
        ConsumerAppRegistry apps,
        LoraAssetService loras,
        ILogger<JobService> log)
    {
        _opts = options.Value;
        _queue = queue;
        _events = events;
        _artifacts = artifacts;
        _ws = ws;
        _persist = persist;
        _fleet = fleet;
        _ops = ops;
        _apps = apps;
        _loras = loras;
        _log = log;
    }

    public JobRecord CreateJob(
        string appId,
        string capability,
        string tier,
        string kind,
        JsonElement payload,
        string? jobId = null,
        int? queuePriority = null)
    {
        var id = string.IsNullOrWhiteSpace(jobId) ? Guid.NewGuid().ToString() : jobId.Trim();
        var tierNorm = string.IsNullOrWhiteSpace(tier) ? "bulk" : tier.Trim();
        var job = new JobRecord
        {
            JobId = id,
            AppId = appId,
            Capability = capability.Trim(),
            Tier = tierNorm,
            QueuePriority = queuePriority,
            Kind = string.IsNullOrWhiteSpace(kind) ? JobKind.Image : kind.Trim(),
            PayloadJson = payload.GetRawText(),
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _queue.Enqueue(job);
        _persist.MarkDirty();
        return job;
    }

    public Task<JobRecord?> ClaimAsync(string capability, string tier, string workerId, string? workerHostname, CancellationToken ct)
    {
        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        if (!WorkerClaimPolicy.CapabilityIsClaimable(worker, capability))
            return Task.FromResult<JobRecord?>(null);

        var lease = TimeSpan.FromSeconds(Math.Max(60, _opts.LeaseSeconds));
        var canClaim = BuildCanClaimPredicate(workerHostname);
        var job = _queue.TryClaim(capability, tier, workerId, workerHostname, lease, canClaim);
        return job == null ? Task.FromResult<JobRecord?>(null) : EmitClaimStartedAsync(job, workerId, ct);
    }

    public Task<JobRecord?> ClaimAnyAsync(
        IReadOnlyList<string> capabilities,
        string workerId,
        string? workerHostname,
        CancellationToken ct)
    {
        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        var readyCaps = WorkerClaimPolicy.ClaimableCapabilities(worker);
        if (readyCaps.Count == 0)
            return Task.FromResult<JobRecord?>(null);

        var lease = TimeSpan.FromSeconds(Math.Max(60, _opts.LeaseSeconds));
        var canClaim = BuildCanClaimPredicate(workerHostname);
        var job = _queue.TryClaimAny(readyCaps, workerId, workerHostname, lease, canClaim);
        return job == null ? Task.FromResult<JobRecord?>(null) : EmitClaimStartedAsync(job, workerId, ct);
    }

    /// <summary>
    /// Explain why a fresh, claim-ready worker received no job. Returned through
    /// response headers so older agents keep treating an empty claim as HTTP 204,
    /// while newer agents can distinguish an empty queue from a blocked queue and
    /// self-heal missing model/LoRA inventory.
    /// </summary>
    public ClaimDiagnostics GetClaimDiagnostics(
        string? workerHostname,
        string? capability = null,
        string? tier = null)
    {
        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        var readyCaps = WorkerClaimPolicy.ClaimableCapabilities(worker);
        if (!string.IsNullOrWhiteSpace(capability))
        {
            readyCaps = WorkerClaimPolicy.CapabilityIsClaimable(worker, capability)
                ? [capability.Trim()]
                : [];
        }
        if (readyCaps.Count == 0)
            return new ClaimDiagnostics(0, 0, 0, 0, []);

        var capSet = readyCaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tierFilter = string.IsNullOrWhiteSpace(tier) || tier == "*" ? null : tier.Trim();
        var modelGate = BuildModelGate(workerHostname);
        var loraGate = BuildLoraGate(workerHostname);
        var queued = _queue.SnapshotJobs()
            .Where(j => j.Status == JobStatus.Queued)
            .Where(j => capSet.Contains(j.Capability))
            .Where(j => tierFilter == null || string.Equals(j.Tier, tierFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var blockedPaused = 0;
        var blockedModel = 0;
        var blockedLora = 0;
        var missingLoras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in queued)
        {
            if (_apps.IsPaused(job.AppId))
            {
                blockedPaused++;
                continue;
            }
            if (!modelGate(job))
            {
                blockedModel++;
                continue;
            }
            if (loraGate(job))
                continue;

            blockedLora++;
            foreach (var missing in MissingWorkerLoras(workerHostname, job))
                missingLoras.Add(WorkerLoraCompatibility.NormalizeBasename(missing));
        }

        return new ClaimDiagnostics(
            queued.Count,
            blockedPaused,
            blockedModel,
            blockedLora,
            missingLoras.Where(x => !string.IsNullOrWhiteSpace(x)).Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private Func<JobRecord, bool> BuildCanClaimPredicate(string? workerHostname)
    {
        var modelGate = BuildModelGate(workerHostname);
        var loraGate = BuildLoraGate(workerHostname);
        return job => !_apps.IsPaused(job.AppId) && modelGate(job) && loraGate(job);
    }

    private Func<JobRecord, bool> BuildModelGate(string? workerHostname)
    {
        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        var assets = WorkerModelAssets.FromJson(worker?.ModelsJson);
        var hostname = worker?.Hostname ?? workerHostname;
        return job =>
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            if (string.IsNullOrWhiteSpace(model)) return true;
            return WorkerModelCompatibility.CanRunModel(assets, model, hostname, job.Capability);
        };
    }

    private Func<JobRecord, bool> BuildLoraGate(string? workerHostname)
    {
        var hn = workerHostname ?? "";
        // Native Wan pulls LoRAs on-demand after claim — do not block the queue head on check-in inventory.
        if (hn.Contains("wan-native", StringComparison.OrdinalIgnoreCase)
            || hn.StartsWith("loboforge-wan-", StringComparison.OrdinalIgnoreCase))
            return _ => true;

        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        var knownLoras = worker?.KnownLoras ?? [];
        var assets = WorkerModelAssets.FromJson(worker?.ModelsJson);
        return job => WorkerLoraCompatibility.ExtractRequiredLoras(job.PayloadJson).All(req =>
            WorkerLoraCompatibility.WorkerHasLora(knownLoras, assets, req)
            || _loras.AppHasReadyLora(job.AppId, req));
    }

    private IReadOnlyList<string> MissingWorkerLoras(string? workerHostname, JobRecord job)
    {
        var hn = workerHostname ?? "";
        if (hn.Contains("wan-native", StringComparison.OrdinalIgnoreCase)
            || hn.StartsWith("loboforge-wan-", StringComparison.OrdinalIgnoreCase))
            return [];

        var worker = _fleet.TryGetWorkerByHostname(workerHostname);
        var knownLoras = worker?.KnownLoras ?? [];
        var assets = WorkerModelAssets.FromJson(worker?.ModelsJson);
        return WorkerLoraCompatibility.ExtractRequiredLoras(job.PayloadJson)
            .Where(req => !WorkerLoraCompatibility.WorkerHasLora(knownLoras, assets, req))
            .Where(req => !_loras.AppHasReadyLora(job.AppId, req))
            .ToList();
    }

    /// <summary>Extend active lease when worker checks in while busy (prevents upload/complete 404).
    /// Also reclaim if the job fell back to queued mid-run (ECS restart / missed lease renew).</summary>
    public async Task<bool> ExtendLeaseOnCheckInAsync(string workerId, string? jobUuid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(jobUuid)) return false;
        var id = jobUuid.Trim();
        var job = await _persist.TryGetJobAsync(id, ct);
        if (job == null) return false;

        var lease = TimeSpan.FromSeconds(Math.Max(60, _opts.LeaseSeconds));
        if (string.Equals(job.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)
            && job.Status is JobStatus.Leased or JobStatus.Streaming)
            return _queue.TryExtendLease(id, workerId, lease);

        // Worker still holds the job but queue lost the lease (restart / timeout race).
        if (job.Status == JobStatus.Queued)
        {
            var worker = _fleet.TryGetWorkerBusyOnJob(workerId, id);
            if (worker == null) return false;
            if (!_queue.TryReclaimIfQueued(id, workerId, worker.Hostname, lease)) return false;
            _persist.MarkDirty();
            _log.LogWarning(
                "Reclaimed orphaned in-flight job {Job} on check-in worker={Worker} host={Host}",
                id, workerId, worker.Hostname);
            return true;
        }

        return false;
    }

    private async Task<JobRecord?> ResolveJobForWorkerAsync(string jobId, string workerId, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct);
        if (job == null) return null;
        if (string.Equals(job.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)) return job;
        if (job.Status != JobStatus.Queued) return null;

        var worker = _fleet.TryGetWorkerBusyOnJob(workerId, jobId);
        if (worker == null) return null;

        var lease = TimeSpan.FromSeconds(Math.Max(60, _opts.LeaseSeconds));
        if (!_queue.TryReclaimIfQueued(jobId, workerId, worker?.Hostname, lease)) return null;
        _persist.MarkDirty();
        _log.LogWarning(
            "Reclaimed orphaned job {Job} for worker {Worker} host={Host}",
            jobId, workerId, worker?.Hostname);
        return _queue.Get(jobId);
    }

    private async Task<JobRecord?> EmitClaimStartedAsync(JobRecord job, string workerId, CancellationToken ct)
    {
        _persist.MarkDirty();
        _fleet.OnClaim(workerId, job.WorkerHostname, job.Capability, job.Tier, job.JobId);

        var startedAt = DateTimeOffset.UtcNow;
        var manifest = BuildLifecycleManifest(job, "started", startedAt);
        var manifestJson = JsonSerializer.Serialize(manifest);
        await _events.PersistAsync(job.AppId, job.JobId, ForgeEventTypes.Started, manifestJson, startedAt, null, ct);
        await _ws.BroadcastAsync(job.AppId, new
        {
            type = ForgeEventTypes.Started,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = job.JobId,
            manifest,
            started_at = startedAt.ToString("O"),
        }, ForgeEventTypes.Started, ct);

        _log.LogInformation(
            "Job started {Job} app={App} worker={Worker} host={Host}",
            job.JobId, job.AppId, workerId, job.WorkerHostname ?? workerId);
        await PublishOpsJobEventAsync("ops.job.started", job, ct);
        return job;
    }

    public async Task<bool> ReleaseAsync(string jobId, string workerId, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct);
        if (job == null || job.WorkerId != workerId) return false;

        var ok = _queue.TryRelease(jobId, workerId);
        if (!ok) return false;

        _persist.MarkDirty();
        _fleet.OnRelease(workerId, job.WorkerHostname);
        await EmitLifecycleEventAsync(job, ForgeEventTypes.Released, "released", job.WorkerHostname, null, ct);
        await PublishOpsJobEventAsync("ops.job.released", job, ct);
        return true;
    }

    public async Task<int> ProcessExpiredLeasesAsync(CancellationToken ct)
    {
        var expired = _queue.RequeueExpired(DateTimeOffset.UtcNow);
        if (expired.Count == 0) return 0;

        _persist.MarkDirty();
        foreach (var info in expired)
        {
            _fleet.OnTimeout(info.WorkerId, info.WorkerHostname);
            var reason = "lease_timeout";
            await EmitLifecycleEventAsync(
                info.Job,
                ForgeEventTypes.Timeout,
                "timeout",
                info.WorkerHostname,
                reason,
                ct);
            _log.LogWarning(
                "Job lease expired {Job} worker={Worker} host={Host} — requeued",
                info.Job.JobId, info.WorkerId, info.WorkerHostname ?? info.WorkerId);
            await PublishOpsJobEventAsync("ops.job.timeout", info.Job, ct);
        }
        return expired.Count;
    }

    public async Task<(int Removed, List<string> Ids)> PurgeQueuedForAppAsync(
        string appId,
        string? capability,
        bool includeInFlight,
        bool deleteS3,
        CancellationToken ct)
    {
        var app = appId.Trim();
        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        // Remove from the in-memory queue first so workers cannot claim mid-purge.
        // Persist deletions even when S3 cleanup fails — otherwise a later restore
        // resurrects jobs that already disappeared from memory.
        var removed = _queue.RemoveWhere(j =>
        {
            if (!string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase)) return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase)) return false;
            if (j.Status == JobStatus.Queued) return true;
            return includeInFlight && j.Status is JobStatus.Leased or JobStatus.Streaming;
        });
        var ids = removed.Select(j => j.JobId).ToList();
        Exception? s3Error = null;
        if (deleteS3 && ids.Count > 0)
        {
            try
            {
                await _artifacts.DeleteJobArtifactsBatchAsync(ids, ct);
            }
            catch (Exception ex)
            {
                s3Error = ex;
                _log.LogError(ex, "S3 artifact delete failed while purging {Count} job(s) for app {AppId}", ids.Count, app);
            }
        }
        if (ids.Count > 0)
            await _persist.DeleteJobsAsync(ids, ct);
        _persist.MarkDirty();
        if (s3Error != null)
            throw s3Error;
        return (removed.Count, ids);
    }

    /// <summary>Move queued (or in-flight) jobs between consumers without restart — updates in-memory queue + persistence flush.</summary>
    public int ReassignConsumer(
        string fromAppId,
        string toAppId,
        string? capability = null,
        string? status = null,
        bool orchestratorCaptionOnly = false)
    {
        var from = fromAppId.Trim();
        var to = toAppId.Trim();
        if (from.Length == 0 || to.Length == 0) return 0;

        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status.Trim();

        var count = _queue.ReassignAppIdWhere(j =>
        {
            if (!string.Equals(j.AppId, from, StringComparison.OrdinalIgnoreCase)) return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase)) return false;
            if (statusFilter != null && !string.Equals(j.Status, statusFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            if (orchestratorCaptionOnly && !IsOrchestratorCaptionJob(j)) return false;
            return true;
        }, to);

        if (count > 0)
        {
            _persist.MarkDirty();
            _log.LogInformation(
                "Reassigned {Count} job(s) from {From} to {To} cap={Cap} status={Status} orchestratorOnly={Orch}",
                count, from, to, cap ?? "*", statusFilter ?? "*", orchestratorCaptionOnly);
        }
        return count;
    }

    /// <summary>Move queued jobs between tiers without restart — updates in-memory queue + persistence flush.</summary>
    public Task<int> RetierQueuedAsync(
        string? appId,
        string? capability,
        string fromTier,
        string toTier,
        CancellationToken ct)
    {
        var from = fromTier.Trim();
        var to = toTier.Trim();
        if (from.Length == 0 || to.Length == 0)
            return Task.FromResult(0);

        var app = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var clearPriority = string.Equals(to, "bulk", StringComparison.OrdinalIgnoreCase);

        var jobs = _queue.SnapshotJobs()
            .Where(j => j.Status == JobStatus.Queued)
            .Where(j => string.Equals(j.Tier, from, StringComparison.OrdinalIgnoreCase))
            .Where(j => app == null || string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase))
            .Where(j => cap == null || string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var count = 0;
        foreach (var job in jobs)
        {
            var updated = new JobRecord
            {
                JobId = job.JobId,
                AppId = job.AppId,
                Capability = job.Capability,
                Tier = to,
                QueuePriority = clearPriority ? null : job.QueuePriority,
                Kind = job.Kind,
                PayloadJson = job.PayloadJson,
                Status = job.Status,
                WorkerId = job.WorkerId,
                WorkerHostname = job.WorkerHostname,
                CreatedAt = job.CreatedAt,
                LeasedAt = job.LeasedAt,
                LeasedUntil = job.LeasedUntil,
                CompletedAt = job.CompletedAt,
                OutputUrl = job.OutputUrl,
                OutputContentType = job.OutputContentType,
                TextReply = job.TextReply,
                Error = job.Error,
            };
            if (_queue.TryUpdate(updated))
                count++;
        }

        if (count > 0)
        {
            _persist.MarkDirty();
            _log.LogInformation(
                "Retiered {Count} queued job(s) from {From} to {To} app={App} cap={Cap}",
                count, from, to, app ?? "*", cap ?? "*");
        }
        return Task.FromResult(count);
    }

    static bool IsOrchestratorCaptionJob(JobRecord job)
    {
        if (!string.Equals(job.Capability, "caption", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var doc = JsonDocument.Parse(job.PayloadJson);
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || !string.Equals(typeEl.GetString(), "assign_job", StringComparison.OrdinalIgnoreCase))
                return false;
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return string.Equals(model, "joycaption", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task<JobRecord?> GetJobForAppAsync(string jobId, string appId, CancellationToken ct) =>
        GetJobOwnedByAppAsync(jobId, appId, ct);

    public async Task<(int Removed, List<string> Ids)> CancelQueuedForAppAsync(
        string appId,
        string? capability,
        bool deleteArtifacts,
        CancellationToken ct) =>
        await PurgeQueuedForAppAsync(appId, capability, includeInFlight: false, deleteS3: deleteArtifacts, ct);

    public async Task<(int Cancelled, List<string> Ids)> CancelMatchingQueuedAsync(
        string? appId,
        string? capability,
        string? externalIdContains,
        string? payloadContains,
        bool includeInFlight,
        CancellationToken ct)
    {
        var app = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var extContains = string.IsNullOrWhiteSpace(externalIdContains) ? null : externalIdContains.Trim();
        var payloadSub = string.IsNullOrWhiteSpace(payloadContains) ? null : payloadContains.Trim();

        bool Matches(JobRecord j)
        {
            if (app != null && !string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase))
                return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase))
                return false;
            var payload = j.PayloadJson ?? "";
            if (payloadSub != null
                && payload.IndexOf(payloadSub, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (extContains != null)
            {
                var externalId = JobPayloadReader.ExtractExternalId(payload);
                if (externalId == null
                    || externalId.IndexOf(extContains, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }
            return true;
        }

        var removed = _queue.RemoveWhere(j => j.Status == JobStatus.Queued && Matches(j));
        var ids = removed.Select(j => j.JobId).ToList();
        if (ids.Count > 0)
        {
            await _persist.DeleteJobsAsync(ids, ct);
            _persist.MarkDirty();
        }

        var cancelled = removed.Count;
        if (includeInFlight)
        {
            var inFlight = _queue.SnapshotJobs()
                .Where(j => Matches(j) && j.Status is JobStatus.Leased or JobStatus.Streaming)
                .ToList();
            foreach (var job in inFlight)
            {
                job.Status = JobStatus.Failed;
                job.Error = "cancelled_by_ops";
                job.CompletedAt = DateTimeOffset.UtcNow;
                _queue.TryUpdate(job);
                _fleet.OnFail(job.WorkerId, job.WorkerHostname);
                await EmitLifecycleEventAsync(job, ForgeEventTypes.Failed, "failed", job.WorkerHostname, job.Error, ct);
                await PublishOpsJobEventAsync("ops.job.cancelled", job, ct);
                ids.Add(job.JobId);
                cancelled++;
            }
            if (inFlight.Count > 0)
                _persist.MarkDirty();
        }

        if (cancelled > 0)
        {
            _log.LogInformation(
                "Ops cancelled {Count} job(s) app={App} external_id_contains={Ext} payload_contains={Payload} include_in_flight={InFlight}",
                cancelled, app ?? "*", extContains ?? "*", payloadSub ?? "*", includeInFlight);
        }
        return (cancelled, ids);
    }

    /// <summary>
    /// Manual moderation: cancel every queued and (optionally) in-flight job for a specific consumer
    /// app whose prompt text contains an ops-entered keyword (case-insensitive literal substring).
    /// Both <paramref name="appId"/> and <paramref name="keyword"/> are required by the caller. When
    /// <paramref name="dryRun"/> is true nothing is mutated — only the matching ids/count are returned.
    /// In-flight jobs are marked failed with <see cref="CancelledByOps"/> so a late worker completion
    /// cannot resurrect them. Never pauses the app or quarantines workers.
    /// </summary>
    public async Task<(int Matched, int Cancelled, List<string> Ids, bool Executed)> CancelByAppKeywordAsync(
        string appId,
        string keyword,
        string? capability,
        bool includeInFlight,
        bool dryRun,
        bool deleteS3,
        CancellationToken ct)
    {
        var app = appId.Trim();
        var kw = keyword.Trim();
        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        if (app.Length == 0 || kw.Length == 0)
            return (0, 0, new List<string>(), false);

        bool Matches(JobRecord j)
        {
            if (!string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase)) return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase)) return false;
            var text = JobPayloadReader.ExtractSearchablePromptText(j.PayloadJson);
            return text.Contains(kw, StringComparison.OrdinalIgnoreCase);
        }

        var eligibleStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { JobStatus.Queued };
        if (includeInFlight)
        {
            eligibleStatuses.Add(JobStatus.Leased);
            eligibleStatuses.Add(JobStatus.Streaming);
        }

        var matched = _queue.SnapshotJobs()
            .Where(j => eligibleStatuses.Contains(j.Status) && Matches(j))
            .ToList();
        var matchedIds = matched.Select(j => j.JobId).ToList();

        if (dryRun)
            return (matched.Count, 0, matchedIds, false);

        var queuedIds = matched
            .Where(j => string.Equals(j.Status, JobStatus.Queued, StringComparison.OrdinalIgnoreCase))
            .Select(j => j.JobId)
            .ToList();

        var cancelled = 0;
        if (queuedIds.Count > 0)
        {
            var idSet = new HashSet<string>(queuedIds, StringComparer.OrdinalIgnoreCase);
            var removed = _queue.RemoveWhere(j =>
                idSet.Contains(j.JobId) && string.Equals(j.Status, JobStatus.Queued, StringComparison.OrdinalIgnoreCase));
            cancelled += removed.Count;
            if (deleteS3 && removed.Count > 0)
                await _artifacts.DeleteJobArtifactsBatchAsync(removed.Select(j => j.JobId).ToList(), ct);
            await _persist.DeleteJobsAsync(removed.Select(j => j.JobId).ToList(), ct);
            _persist.MarkDirty();
        }

        if (includeInFlight)
        {
            var inFlight = _queue.SnapshotJobs()
                .Where(j => j.Status is JobStatus.Leased or JobStatus.Streaming && Matches(j))
                .ToList();
            foreach (var job in inFlight)
            {
                job.Status = JobStatus.Failed;
                job.Error = CancelledByOps;
                job.CompletedAt = DateTimeOffset.UtcNow;
                _queue.TryUpdate(job);
                _fleet.OnFail(job.WorkerId, job.WorkerHostname);
                await EmitLifecycleEventAsync(job, ForgeEventTypes.Failed, "failed", job.WorkerHostname, job.Error, ct);
                await PublishOpsJobEventAsync("ops.job.cancelled", job, ct);
                if (deleteS3)
                {
                    try { await _artifacts.DeleteJobArtifactsAsync(job.JobId, ct); }
                    catch (Exception ex) { _log.LogWarning(ex, "Artifact delete failed for cancelled in-flight job {Job}", job.JobId); }
                }
                cancelled++;
            }
            if (inFlight.Count > 0)
                _persist.MarkDirty();
        }

        _log.LogInformation(
            "Ops keyword-cancelled {Count} job(s) app={App} keyword_len={KwLen} capability={Cap} include_in_flight={InFlight}",
            cancelled, app, kw.Length, cap ?? "*", includeInFlight);
        return (matched.Count, cancelled, matchedIds, true);
    }

    /// <summary>
    /// Moderation delete of a finished (completed/failed) job before or after consumer pickup:
    /// removes the output artifact from S3/local storage, deletes persisted completion events so a
    /// consumer replay cannot receive the output, and removes the job record. Idempotent — a repeat
    /// call for a missing job still attempts artifact/event cleanup. Refuses to delete a job that is
    /// still queued or in-flight unless <paramref name="allowActive"/> is set (callers should cancel
    /// instead). This is manual moderation — no scanning or content analysis is performed.
    /// </summary>
    public async Task<JobDeletionResult> DeleteCompletedJobAsync(
        string jobId, bool allowActive, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return new JobDeletionResult(false, null, null, false, 0, false);
        var id = jobId.Trim();
        var job = await _persist.TryGetJobAsync(id, ct);

        if (job != null
            && job.Status is JobStatus.Queued or JobStatus.Leased or JobStatus.Streaming
            && !allowActive)
        {
            return new JobDeletionResult(true, job.Status, job.AppId, false, 0, true);
        }

        // Delete the output artifact first (idempotent: S3/local delete of a missing prefix is a no-op).
        await _artifacts.DeleteJobArtifactsAsync(id, ct);

        var eventsDeleted = await _events.DeleteByJobAsync(id, ct);
        var removed = job != null ? await _persist.DeleteJobsAsync(new[] { id }, ct) : 0;
        _persist.MarkDirty();

        if (job != null)
        {
            _log.LogWarning(
                "Ops deleted finished job {Job} app={App} status={Status} events={Events}",
                id, job.AppId, job.Status, eventsDeleted);
            await PublishOpsJobEventAsync("ops.job.deleted", job, ct);
        }

        return new JobDeletionResult(job != null, job?.Status, job?.AppId, removed > 0 || job != null, eventsDeleted, false);
    }

    public Task<(int Requeued, List<string> Ids)> RequeueFailedAsync(
        string? capability,
        string? appId,
        string? errorContains,
        int? limit,
        DateTimeOffset? failedSince,
        CancellationToken ct,
        string? jobId = null)
    {
        var cap = string.IsNullOrWhiteSpace(capability) ? null : capability.Trim();
        var app = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var errSub = string.IsNullOrWhiteSpace(errorContains) ? null : errorContains.Trim();
        var job = string.IsNullOrWhiteSpace(jobId) ? null : jobId.Trim();
        if (limit is < 1)
            limit = null;

        bool Matches(JobRecord j)
        {
            if (j.Status != JobStatus.Failed) return false;
            if (job != null && !string.Equals(j.JobId, job, StringComparison.OrdinalIgnoreCase))
                return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase))
                return false;
            if (app != null && !string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase))
                return false;
            if (errSub != null
                && (j.Error ?? "").IndexOf(errSub, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
            if (failedSince != null && (j.CompletedAt ?? j.CreatedAt) < failedSince.Value)
                return false;
            return true;
        }

        var requeued = _queue.RequeueFailedWhere(Matches, limit);
        var ids = requeued.Select(j => j.JobId).ToList();
        if (ids.Count > 0)
        {
            _persist.MarkDirty();
            _log.LogInformation(
                "Ops requeued {Count} failed job(s) job={Job} capability={Cap} app={App} error_contains={Err} failed_since={Since}",
                ids.Count, job ?? "*", cap ?? "*", app ?? "*", errSub ?? "*", failedSince?.ToString("O") ?? "*");
        }
        return Task.FromResult((ids.Count, ids));
    }

    public async Task<(JobRecord Job, bool Removed)?> CancelJobForAppAsync(
        string jobId,
        string appId,
        bool includeInFlight,
        bool deleteArtifacts,
        CancellationToken ct)
    {
        var job = await GetJobOwnedByAppAsync(jobId, appId, ct);
        if (job == null) return null;

        if (job.Status == JobStatus.Queued)
        {
            var removed = _queue.RemoveWhere(j => string.Equals(j.JobId, job.JobId, StringComparison.OrdinalIgnoreCase));
            if (removed.Count == 0) return null;
            if (deleteArtifacts)
                await _artifacts.DeleteJobArtifactsBatchAsync([job.JobId], ct);
            await _persist.DeleteJobsAsync([job.JobId], ct);
            _persist.MarkDirty();
            _log.LogInformation("Consumer cancelled queued job {Job} app={App}", job.JobId, appId);
            return (removed[0], true);
        }

        if (job.Status is JobStatus.Leased or JobStatus.Streaming && includeInFlight)
        {
            job.Status = JobStatus.Failed;
            job.Error = "cancelled_by_consumer";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _queue.TryUpdate(job);
            _persist.MarkDirty();
            _fleet.OnFail(job.WorkerId, job.WorkerHostname);
            await EmitLifecycleEventAsync(job, ForgeEventTypes.Failed, "failed", job.WorkerHostname, job.Error, ct);
            await PublishOpsJobEventAsync("ops.job.cancelled", job, ct);
            _log.LogInformation("Consumer cancelled in-flight job {Job} app={App}", job.JobId, appId);
            return (job, false);
        }

        return null;
    }

    public async Task<(JobRecord Job, bool Removed)?> CancelJobAsync(
        string jobId,
        bool includeInFlight,
        bool deleteArtifacts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return null;
        var job = await _persist.TryGetJobAsync(jobId.Trim(), ct);
        if (job == null) return null;

        if (job.Status == JobStatus.Queued)
        {
            var removed = _queue.RemoveWhere(j => string.Equals(j.JobId, job.JobId, StringComparison.OrdinalIgnoreCase));
            if (removed.Count == 0) return null;
            if (deleteArtifacts)
                await _artifacts.DeleteJobArtifactsBatchAsync([job.JobId], ct);
            await _persist.DeleteJobsAsync([job.JobId], ct);
            _persist.MarkDirty();
            _log.LogInformation("Ops cancelled queued job {Job} app={App}", job.JobId, job.AppId);
            return (removed[0], true);
        }

        if (job.Status is JobStatus.Leased or JobStatus.Streaming && includeInFlight)
        {
            job.Status = JobStatus.Failed;
            job.Error = "cancelled_by_ops";
            job.CompletedAt = DateTimeOffset.UtcNow;
            _queue.TryUpdate(job);
            _persist.MarkDirty();
            _fleet.OnFail(job.WorkerId, job.WorkerHostname);
            await EmitLifecycleEventAsync(job, ForgeEventTypes.Failed, "failed", job.WorkerHostname, job.Error, ct);
            await PublishOpsJobEventAsync("ops.job.cancelled", job, ct);
            _log.LogInformation("Ops cancelled in-flight job {Job} app={App}", job.JobId, job.AppId);
            return (job, false);
        }

        return null;
    }

    private async Task<JobRecord?> GetJobOwnedByAppAsync(string jobId, string appId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(appId)) return null;
        var job = await _persist.TryGetJobAsync(jobId.Trim(), ct);
        if (job == null || !string.Equals(job.AppId, appId.Trim(), StringComparison.OrdinalIgnoreCase))
            return null;
        return job;
    }

    public async Task<bool> PushStreamTokenAsync(string jobId, string workerId, string delta, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct);
        if (job == null || job.WorkerId != workerId) return false;
        if (job.Kind != JobKind.TextStream) return false;

        job.Status = JobStatus.Streaming;
        _queue.TryUpdate(job);
        _persist.MarkDirty();

        lock (_streamBuffers)
        {
            if (!_streamBuffers.TryGetValue(jobId, out var sb))
            {
                sb = new StringBuilder();
                _streamBuffers[jobId] = sb;
            }
            sb.Append(delta);
        }

        await _ws.BroadcastAsync(job.AppId, new
        {
            type = ForgeEventTypes.StreamToken,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = jobId,
            delta,
        }, ForgeEventTypes.StreamToken, ct);
        return true;
    }


    public async Task<bool> SaveInputStreamAsync(
        string jobId, string fileName, string contentType, Stream body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return false;
        await _artifacts.SaveInputStreamAsync(jobId.Trim(), fileName, contentType, body, ct);
        _persist.MarkDirty();
        return true;
    }

    public async Task<(Stream Stream, string ContentType)?> OpenInputStreamAsync(
        string jobId, string fileName, CancellationToken ct)
    {
        var opened = await _artifacts.TryOpenInputStreamAsync(jobId, fileName, ct);
        if (opened == null) return null;
        return (opened.Value.Stream, opened.Value.ContentType);
    }

    public async Task<bool> SaveOutputStreamAsync(
        string jobId, string workerId, string fileName, string contentType, Stream body, CancellationToken ct)
    {
        var job = await ResolveJobForWorkerAsync(jobId, workerId, ct);
        if (job == null) return false;
        if (IsCancelledTerminal(job))
        {
            _log.LogWarning("Dropping late output upload for cancelled job {Job} app={App}", job.JobId, job.AppId);
            return false;
        }

        await using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0) return false;
        buffer.Position = 0;
        var (url, ctOut) = await _artifacts.SaveStreamAsync(jobId, fileName, contentType, buffer, ct);
        job.OutputUrl = url;
        job.OutputContentType = ctOut;
        _queue.TryUpdate(job);
        _persist.MarkDirty();
        return true;
    }

    public async Task<JobRecord?> CompleteAsync(string jobId, string workerId, string? textReply, CancellationToken ct)
    {
        var job = await ResolveJobForWorkerAsync(jobId, workerId, ct);
        if (job == null) return null;
        if (IsCancelledTerminal(job))
        {
            _log.LogWarning("Dropping late completion for cancelled job {Job} app={App}", job.JobId, job.AppId);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(textReply))
            job.TextReply = textReply;
        else
        {
            lock (_streamBuffers)
            {
                if (_streamBuffers.TryGetValue(jobId, out var sb))
                    job.TextReply = sb.ToString();
            }
        }

        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        _queue.TryUpdate(job);
        _persist.MarkDirty();
        _fleet.OnComplete(workerId, job.WorkerHostname);

        await PublishCompletedEventAsync(job, ct);

        if (job.Kind == JobKind.TextStream)
        {
            await _ws.BroadcastAsync(job.AppId, new
            {
                type = ForgeEventTypes.StreamDone,
                event_id = Guid.NewGuid().ToString(),
                app_id = job.AppId,
                job_id = job.JobId,
                text = job.TextReply ?? "",
                completed_at = job.CompletedAt.Value.ToString("O"),
            }, ForgeEventTypes.StreamDone, ct);
            lock (_streamBuffers) _streamBuffers.Remove(jobId);
        }

        _log.LogInformation("Job completed {Job} app={App} kind={Kind}", job.JobId, job.AppId, job.Kind);
        await PublishOpsJobEventAsync("ops.job.completed", job, ct);
        return job;
    }

    /// <summary>
    /// Re-broadcast <c>forge.job.completed</c> for an already-completed job so consumers that
    /// missed the original WS/HTTP event can apply the artifact (live-queue orphans).
    /// </summary>
    public async Task<JobRecord?> ReemitCompletionAsync(string jobId, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct)
                  ?? _queue.Get(jobId)
                  ?? _queue.SnapshotJobs()
                      .FirstOrDefault(j => j.JobId.StartsWith(jobId.Trim(), StringComparison.OrdinalIgnoreCase));
        if (job == null) return null;
        if (!string.Equals(job.Status, JobStatus.Completed, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(job.OutputUrl) && job.Kind != JobKind.TextStream
            && string.IsNullOrWhiteSpace(job.TextReply))
        {
            _log.LogWarning("Reemit skipped for {Job}: completed but no output_url/text", job.JobId);
            return null;
        }

        // Fresh completed_at so HTTP poll `since` windows include this reemit.
        job.CompletedAt = DateTimeOffset.UtcNow;
        _queue.TryUpdate(job);
        _persist.MarkDirty();

        await PublishCompletedEventAsync(job, ct);
        await PublishOpsJobEventAsync("ops.job.completed", job, ct);
        _log.LogInformation("Re-emitted completion for {Job} app={App}", job.JobId, job.AppId);
        return job;
    }

    private async Task PublishCompletedEventAsync(JobRecord job, CancellationToken ct)
    {
        var at = job.CompletedAt ?? DateTimeOffset.UtcNow;
        var manifest = BuildManifest(job);
        var manifestJson = JsonSerializer.Serialize(manifest);
        await _events.PersistAsync(job.AppId, job.JobId, ForgeEventTypes.Completed, manifestJson, at, null, ct);
        await _ws.BroadcastAsync(job.AppId, new
        {
            type = ForgeEventTypes.Completed,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = job.JobId,
            manifest,
            completed_at = at.ToString("O"),
        }, ForgeEventTypes.Completed, ct);
    }

    public async Task<JobRecord?> FailAsync(string jobId, string workerId, string error, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct);
        if (job == null || job.WorkerId != workerId) return null;

        if (WorkerModelCompatibility.IsNeverFailCapability(job.Capability))
        {
            _log.LogWarning(
                "Redirecting fail→release for {Cap} job {Job}: {Error}",
                job.Capability, job.JobId, error);
            if (!await ReleaseAsync(jobId, workerId, ct))
                return null;
            return await _persist.TryGetJobAsync(jobId, ct);
        }

        job.Status = JobStatus.Failed;
        job.Error = error;
        job.CompletedAt = DateTimeOffset.UtcNow;
        _queue.TryUpdate(job);
        _persist.MarkDirty();
        _fleet.OnFail(workerId, job.WorkerHostname);

        var manifest = BuildLifecycleManifest(job, "failed", job.CompletedAt.Value, error);
        var manifestJson = JsonSerializer.Serialize(manifest);
        await _events.PersistAsync(job.AppId, job.JobId, ForgeEventTypes.Failed, manifestJson, job.CompletedAt.Value, error, ct);
        await _ws.BroadcastAsync(job.AppId, new
        {
            type = ForgeEventTypes.Failed,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = job.JobId,
            manifest,
            error,
            completed_at = job.CompletedAt.Value.ToString("O"),
        }, ForgeEventTypes.Failed, ct);
        lock (_streamBuffers) _streamBuffers.Remove(jobId);
        await PublishOpsJobEventAsync("ops.job.failed", job, ct);
        return job;
    }

    private Task PublishOpsJobEventAsync(string eventType, JobRecord job, CancellationToken ct) =>
        _ops.PublishAsync(eventType, new
        {
            type = eventType,
            job_id = job.JobId,
            app_id = job.AppId,
            capability = job.Capability,
            tier = job.Tier,
            status = job.Status,
            worker_id = job.WorkerId,
            hostname = job.WorkerHostname,
            error = job.Error,
            at = DateTimeOffset.UtcNow.ToString("O"),
        }, ct);

    private Task EmitLifecycleEventAsync(
        JobRecord job,
        string eventType,
        string status,
        string? workerHostname,
        string? error,
        CancellationToken ct = default) =>
        EmitLifecycleEventAsync(job, eventType, status, workerHostname, error, DateTimeOffset.UtcNow, ct);

    private async Task EmitLifecycleEventAsync(
        JobRecord job,
        string eventType,
        string status,
        string? workerHostname,
        string? error,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var manifest = BuildLifecycleManifest(job, status, at, error, workerHostname);
        var manifestJson = JsonSerializer.Serialize(manifest);
        await _events.PersistAsync(job.AppId, job.JobId, eventType, manifestJson, at, error, ct);
        await _ws.BroadcastAsync(job.AppId, new
        {
            type = eventType,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = job.JobId,
            manifest,
            error,
            completed_at = at.ToString("O"),
        }, eventType, ct);
    }

    private static object BuildLifecycleManifest(
        JobRecord job,
        string status,
        DateTimeOffset at,
        string? error = null,
        string? workerHostname = null)
    {
        var host = workerHostname ?? job.WorkerHostname ?? job.WorkerId;
        return new
        {
            job_id = job.JobId,
            status,
            tenant_id = job.AppId,
            capability = job.Capability,
            tier = job.Tier,
            kind = job.Kind,
            worker_id = job.WorkerId,
            hostname = host,
            leased_until = job.LeasedUntil?.ToString("O"),
            started_at = status == "started" ? at.ToString("O") : null,
            completed_at = status is "failed" or "timeout" or "released" ? at.ToString("O") : null,
            error,
            requeued = status is "timeout" or "released",
        };
    }

    private static object BuildManifest(JobRecord job)
    {
        if (job.Kind == JobKind.TextStream)
        {
            return new
            {
                job_id = job.JobId,
                status = "completed",
                tenant_id = job.AppId,
                capability = job.Capability,
                tier = job.Tier,
                kind = job.Kind,
                text = job.TextReply ?? "",
                completed_at = job.CompletedAt?.ToString("O"),
                worker_id = job.WorkerId,
                hostname = job.WorkerHostname ?? job.WorkerId,
            };
        }

        return new Dictionary<string, object?>
        {
            ["job_id"] = job.JobId,
            ["status"] = "completed",
            ["tenant_id"] = job.AppId,
            ["capability"] = job.Capability,
            ["tier"] = job.Tier,
            ["kind"] = job.Kind,
            ["outputs"] = string.IsNullOrWhiteSpace(job.OutputUrl)
                ? Array.Empty<object>()
                : new[] { new { key = job.OutputUrl, url = job.OutputUrl, content_type = job.OutputContentType ?? "application/octet-stream" } },
            ["completed_at"] = job.CompletedAt?.ToString("O"),
            ["worker_id"] = job.WorkerId,
            ["hostname"] = job.WorkerHostname ?? job.WorkerId,
        };
    }
}
