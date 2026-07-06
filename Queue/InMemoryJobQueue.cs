using System.Collections.Concurrent;
using EventForge.Core;

namespace EventForge.Queue;

public sealed class ExpiredLeaseInfo
{
    public required JobRecord Job { get; init; }
    public required string WorkerId { get; init; }
    public string? WorkerHostname { get; init; }
}

public sealed class InMemoryJobQueue
{
    private readonly object _lock = new();
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _queuedOrder = new();

    public int QueuedCount
    {
        get { lock (_lock) return _queuedOrder.Count; }
    }

    public int TotalCount
    {
        get { lock (_lock) return _jobs.Count; }
    }

    public void Load(JobRecord job)
    {
        lock (_lock)
        {
            _jobs[job.JobId] = Clone(job);
            if (job.Status == JobStatus.Queued)
                _queuedOrder.Add(job.JobId);
        }
    }

    public JobRecord Enqueue(JobRecord job)
    {
        lock (_lock)
        {
            if (_jobs.TryGetValue(job.JobId, out var existing)
                && existing.Status == JobStatus.Queued
                && _queuedOrder.Contains(job.JobId))
            {
                return Clone(existing);
            }

            _jobs[job.JobId] = Clone(job);
            if (!_queuedOrder.Contains(job.JobId))
                _queuedOrder.Add(job.JobId);
            return Clone(job);
        }
    }

    /// <summary>Renew lease while worker is still processing (check-in with current_job_uuid).</summary>
    public bool TryExtendLease(string jobId, string workerId, TimeSpan lease)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return false;
            if (job.Status is not (JobStatus.Leased or JobStatus.Streaming)) return false;
            if (!string.Equals(job.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)) return false;
            job.LeasedUntil = DateTimeOffset.UtcNow.Add(lease);
            return true;
        }
    }

    public JobRecord? TryClaim(string capability, string tier, string workerId, string? workerHostname, TimeSpan lease, Func<JobRecord, bool>? canClaim = null)
    {
        lock (_lock)
        {
            RequeueExpiredLocked(DateTimeOffset.UtcNow);
            for (var i = 0; i < _queuedOrder.Count; i++)
            {
                var id = _queuedOrder[i];
                if (!_jobs.TryGetValue(id, out var job)) continue;
                if (job.Status != JobStatus.Queued) continue;
                if (!CapabilityMatches(job, capability, tier)) continue;
                if (canClaim != null && !canClaim(job)) continue;

                return LeaseJobLocked(job, workerId, workerHostname, lease, i);
            }
            return null;
        }
    }

    public JobRecord? TryClaimAny(
        IReadOnlyList<string> capabilities,
        string workerId,
        string? workerHostname,
        TimeSpan lease,
        Func<JobRecord, bool>? canClaim = null)
    {
        lock (_lock)
        {
            RequeueExpiredLocked(DateTimeOffset.UtcNow);
            var capSet = new HashSet<string>(
                capabilities.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (capSet.Count == 0) return null;

            JobRecord? bestJob = null;
            var bestIndex = -1;

            for (var i = 0; i < _queuedOrder.Count; i++)
            {
                var id = _queuedOrder[i];
                if (!_jobs.TryGetValue(id, out var job)) continue;
                if (job.Status != JobStatus.Queued) continue;
                if (!capSet.Contains(job.Capability)) continue;
                if (canClaim != null && !canClaim(job)) continue;

                if (bestJob == null || JobQueueOrdering.CompareForClaim(job, bestJob) < 0)
                {
                    bestJob = job;
                    bestIndex = i;
                }
            }

            if (bestJob == null || bestIndex < 0) return null;
            return LeaseJobLocked(bestJob, workerId, workerHostname, lease, bestIndex);
        }
    }

    private JobRecord LeaseJobLocked(
        JobRecord job,
        string workerId,
        string? workerHostname,
        TimeSpan lease,
        int queueIndex)
    {
        job.Status = JobStatus.Leased;
        job.WorkerId = workerId;
        job.WorkerHostname = string.IsNullOrWhiteSpace(workerHostname) ? null : workerHostname.Trim();
        job.LeasedUntil = DateTimeOffset.UtcNow.Add(lease);
        _queuedOrder.RemoveAt(queueIndex);
        return Clone(job);
    }

    public JobRecord? Get(string jobId)
    {
        lock (_lock)
        {
            return _jobs.TryGetValue(jobId, out var job) ? Clone(job) : null;
        }
    }


    public bool TryRelease(string jobId, string workerId)
    {
        lock (_lock)
        {
            if (!_jobs.TryGetValue(jobId, out var job)) return false;
            if (!string.Equals(job.WorkerId, workerId, StringComparison.OrdinalIgnoreCase)) return false;
            if (job.Status is not (JobStatus.Leased or JobStatus.Streaming)) return false;
            job.Status = JobStatus.Queued;
            job.WorkerId = null;
            job.WorkerHostname = null;
            job.LeasedUntil = null;
            if (!_queuedOrder.Contains(job.JobId))
                _queuedOrder.Add(job.JobId);
            return true;
        }
    }

    public bool TryUpdate(JobRecord updated)
    {
        lock (_lock)
        {
            if (!_jobs.ContainsKey(updated.JobId)) return false;
            _jobs[updated.JobId] = Clone(updated);
            return true;
        }
    }

    public IReadOnlyList<JobRecord> SnapshotJobs()
    {
        lock (_lock) return _jobs.Values.Select(Clone).ToList();
    }

    /// <summary>Removes completed/failed jobs older than <paramref name="cutoff"/> from memory.</summary>
    public int PruneTerminalOlderThan(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            var toRemove = _jobs.Values
                .Where(j => j.Status is JobStatus.Completed or JobStatus.Failed)
                .Where(j => (j.CompletedAt ?? j.CreatedAt) < cutoff)
                .Select(j => j.JobId)
                .ToList();
            foreach (var id in toRemove)
            {
                _jobs.Remove(id);
                _queuedOrder.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            }
            return toRemove.Count;
        }
    }

    public IReadOnlyList<JobRecord> RemoveWhere(Func<JobRecord, bool> predicate)
    {
        lock (_lock)
        {
            var removed = new List<JobRecord>();
            foreach (var job in _jobs.Values.Where(predicate).ToList())
            {
                removed.Add(Clone(job));
                _jobs.Remove(job.JobId);
                _queuedOrder.RemoveAll(x => string.Equals(x, job.JobId, StringComparison.OrdinalIgnoreCase));
            }
            return removed;
        }
    }

    public IReadOnlyList<ExpiredLeaseInfo> RequeueExpired(DateTimeOffset now)
    {
        lock (_lock)
        {
            return RequeueExpiredLocked(now);
        }
    }

    private List<ExpiredLeaseInfo> RequeueExpiredLocked(DateTimeOffset now)
    {
        var expired = new List<ExpiredLeaseInfo>();
        foreach (var job in _jobs.Values)
        {
            if (job.Status is not (JobStatus.Leased or JobStatus.Streaming)) continue;
            if (job.LeasedUntil is null || job.LeasedUntil > now) continue;
            expired.Add(new ExpiredLeaseInfo
            {
                Job = Clone(job),
                WorkerId = job.WorkerId ?? "",
                WorkerHostname = job.WorkerHostname,
            });
            job.Status = JobStatus.Queued;
            job.WorkerId = null;
            job.WorkerHostname = null;
            job.LeasedUntil = null;
            if (!_queuedOrder.Contains(job.JobId))
                _queuedOrder.Add(job.JobId);
        }
        return expired;
    }

    private static bool CapabilityMatches(JobRecord job, string capability, string tier)
    {
        if (!string.Equals(job.Capability, capability, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(tier) || tier == "*") return true;
        return string.Equals(job.Tier, tier, StringComparison.OrdinalIgnoreCase);
    }

    private static JobRecord Clone(JobRecord j) => new()
    {
        JobId = j.JobId,
        AppId = j.AppId,
        Capability = j.Capability,
        Tier = j.Tier,
        QueuePriority = j.QueuePriority,
        Kind = j.Kind,
        PayloadJson = j.PayloadJson,
        Status = j.Status,
        WorkerId = j.WorkerId,
        WorkerHostname = j.WorkerHostname,
        CreatedAt = j.CreatedAt,
        LeasedUntil = j.LeasedUntil,
        CompletedAt = j.CompletedAt,
        OutputUrl = j.OutputUrl,
        OutputContentType = j.OutputContentType,
        TextReply = j.TextReply,
        Error = j.Error,
    };
}
