using EventForge.Core;

namespace EventForge.Queue;

/// <summary>
/// Server-side claim ordering: tier lane (admin → vip → normal → bulk),
/// then queue priority within tier, then FIFO by created_at.
/// </summary>
public static class JobQueueOrdering
{
    public static int TierRank(string tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "admin" => 4,
        "vip" => 3,
        "normal" => 2,
        "bulk" => 1,
        _ => 2,
    };

    public static int PriorityFromTier(string tier) => (tier ?? "").Trim().ToLowerInvariant() switch
    {
        "admin" => 2,
        "vip" => 1,
        "normal" => 0,
        "bulk" => -1,
        _ => 0,
    };

    public static int EffectivePriority(JobRecord job) =>
        job.QueuePriority ?? PriorityFromTier(job.Tier);

    /// <summary>Negative when <paramref name="a"/> should be claimed before <paramref name="b"/>.</summary>
    public static int CompareForClaim(JobRecord a, JobRecord b)
    {
        var tierCmp = TierRank(b.Tier).CompareTo(TierRank(a.Tier));
        if (tierCmp != 0) return tierCmp;

        var priCmp = EffectivePriority(b).CompareTo(EffectivePriority(a));
        if (priCmp != 0) return priCmp;

        return a.CreatedAt.CompareTo(b.CreatedAt);
    }
}
