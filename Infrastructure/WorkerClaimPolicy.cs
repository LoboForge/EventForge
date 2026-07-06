using EventForge.Services;

namespace EventForge.Infrastructure;

/// <summary>
/// Job claims use only the worker's last check-in (models + claim_ready_capabilities).
/// Request body capabilities are ignored — check-in is the health report.
/// </summary>
public static class WorkerClaimPolicy
{
    public static bool IsCheckInFresh(WorkerSnapshot? worker)
    {
        if (worker == null) return false;
        return !worker.CheckInStale;
    }

    public static bool CanAttemptClaim(WorkerSnapshot? worker) =>
        worker != null && IsCheckInFresh(worker) && worker.ClaimReadyCapabilities is { Count: > 0 };

    /// <summary>Capabilities the queue may assign — exclusively from check-in claim_ready.</summary>
    public static IReadOnlyList<string> ClaimableCapabilities(WorkerSnapshot? worker)
    {
        if (!CanAttemptClaim(worker)) return Array.Empty<string>();
        return worker!.ClaimReadyCapabilities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool CapabilityIsClaimable(WorkerSnapshot? worker, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return false;
        var cap = capability.Trim();
        return ClaimableCapabilities(worker).Any(c => string.Equals(c, cap, StringComparison.OrdinalIgnoreCase));
    }
}
