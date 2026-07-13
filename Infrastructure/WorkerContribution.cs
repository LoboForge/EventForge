using EventForge.Services;

namespace EventForge.Infrastructure;

public static class WorkerContribution
{
    public static bool IsNonContributing(WorkerSnapshot w)
    {
        return Badges(w).Count > 0;
    }

    public static IReadOnlyList<string> Badges(WorkerSnapshot w)
    {
        var badges = new List<string>();
        var hn = (w.Hostname ?? "").ToLowerInvariant();

        if (w.CheckInStale)
            badges.Add("stale");

        if (w.QueueAccessOk == false)
            badges.Add("queue-blocked");

        if ((!w.ComfyOk) && (hn.Contains("image") || hn.Contains("video")))
            badges.Add("comfy-down");

        var isGen = hn.Contains("loboforge-image")
            || hn.Contains("loboforge-video")
            || hn.Contains("loboforge-ltx")
            || hn.Contains("loboforge-wan")
            || hn.Contains("loboforge-ollama");

        if (w.DiskFreeMb is > 0 and < 12_000 && (hn.Contains("wan") || hn.Contains("video") || hn.Contains("image") || hn.Contains("all")))
            badges.Add("disk-low");

        if (hn.Contains("wan-native") && !w.Busy && w.ClaimReadyCapabilities.Count == 0 && !w.CheckInStale)
            badges.Add("wan-not-ready");

        if (isGen && !w.Busy && w.ClaimReadyCapabilities.Count == 0 && !w.CheckInStale)
            badges.Add("no-claim-ready");

        if (isGen && !w.Busy && w.JobsCompleted == 0 && w.JobsClaimed == 0 && !w.CheckInStale)
            badges.Add("idle-no-jobs");

        if (w.Busy && string.IsNullOrWhiteSpace(w.CurrentJobUuid) && string.IsNullOrWhiteSpace(w.ActiveJobId))
            badges.Add("busy-no-job-id");

        return badges;
    }
}
