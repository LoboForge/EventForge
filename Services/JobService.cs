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

public sealed class JobService
{
    private readonly EventForgeOptions _opts;
    private readonly InMemoryJobQueue _queue;
    private readonly IEventStore _events;
    private readonly IArtifactStore _artifacts;
    private readonly WsConnectionManager _ws;
    private readonly WriteBehindPersistence _persist;
    private readonly WorkerFleetTracker _fleet;
    private readonly OpsEventHub _ops;
    private readonly ConsumerAppRegistry _apps;
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

    private Func<JobRecord, bool> BuildCanClaimPredicate(string? workerHostname)
    {
        var modelGate = BuildModelGate(workerHostname);
        return job => !_apps.IsPaused(job.AppId) && modelGate(job);
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

    /// <summary>Extend active lease when worker checks in while busy (prevents upload/complete 404).</summary>
    public async Task<bool> ExtendLeaseOnCheckInAsync(string workerId, string? jobUuid, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workerId) || string.IsNullOrWhiteSpace(jobUuid)) return false;
        var job = await _persist.TryGetJobAsync(jobUuid.Trim(), ct);
        if (job == null || !string.Equals(job.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)) return false;
        var lease = TimeSpan.FromSeconds(Math.Max(60, _opts.LeaseSeconds));
        return _queue.TryExtendLease(jobUuid.Trim(), workerId, lease);
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
        var removed = _queue.RemoveWhere(j =>
        {
            if (!string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase)) return false;
            if (cap != null && !string.Equals(j.Capability, cap, StringComparison.OrdinalIgnoreCase)) return false;
            if (j.Status == JobStatus.Queued) return true;
            return includeInFlight && j.Status is JobStatus.Leased or JobStatus.Streaming;
        });
        var ids = removed.Select(j => j.JobId).ToList();
        if (deleteS3 && ids.Count > 0)
            await _artifacts.DeleteJobArtifactsBatchAsync(ids, ct);
        if (ids.Count > 0)
            await _persist.DeleteJobsAsync(ids, ct);
        _persist.MarkDirty();
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
        string? externalIdContains,
        string? payloadContains,
        bool includeInFlight,
        CancellationToken ct)
    {
        var app = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        var extContains = string.IsNullOrWhiteSpace(externalIdContains) ? null : externalIdContains.Trim();
        var payloadSub = string.IsNullOrWhiteSpace(payloadContains) ? null : payloadContains.Trim();

        bool Matches(JobRecord j)
        {
            if (app != null && !string.Equals(j.AppId, app, StringComparison.OrdinalIgnoreCase))
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

        var manifest = BuildManifest(job);
        var manifestJson = JsonSerializer.Serialize(manifest);
        var eventType = ForgeEventTypes.Completed;
        await _events.PersistAsync(job.AppId, job.JobId, eventType, manifestJson, job.CompletedAt.Value, null, ct);

        await _ws.BroadcastAsync(job.AppId, new
        {
            type = ForgeEventTypes.Completed,
            event_id = Guid.NewGuid().ToString(),
            app_id = job.AppId,
            job_id = job.JobId,
            manifest,
            completed_at = job.CompletedAt.Value.ToString("O"),
        }, ForgeEventTypes.Completed, ct);

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

    public async Task<JobRecord?> FailAsync(string jobId, string workerId, string error, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId, ct);
        if (job == null || job.WorkerId != workerId) return null;

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
