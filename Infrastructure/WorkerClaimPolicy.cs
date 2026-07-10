using EventForge.Services;

namespace EventForge.Infrastructure;

/// <summary>
/// Job claims use the worker's last check-in (models + claim_ready_capabilities).
/// JoyCaption/Ollama workers may omit claim_ready — infer from inventory-optional caps.
/// </summary>
public static class WorkerClaimPolicy
{
    /// <summary>Capabilities that do not require Comfy/model inventory in check-in.</summary>
    private static readonly HashSet<string> InventoryOptionalCapabilities =
        new(StringComparer.OrdinalIgnoreCase) { "caption", "dolphin", "ollama-chat" };

    public static bool IsCheckInFresh(WorkerSnapshot? worker)
    {
        if (worker == null) return false;
        return !worker.CheckInStale;
    }

    public static IReadOnlyList<string> EffectiveClaimReady(WorkerSnapshot? worker)
    {
        if (worker == null || !IsCheckInFresh(worker)) return [];

        var explicitReady = worker.ClaimReadyCapabilities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (explicitReady.Count > 0) return explicitReady;

        // JoyCaption checks in forge_queue_capabilities=["caption"] but not claim_ready.
        return worker.Capabilities
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Where(InventoryOptionalCapabilities.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool CanAttemptClaim(WorkerSnapshot? worker) =>
        worker != null && EffectiveClaimReady(worker).Count > 0;

    public static IReadOnlyList<string> ClaimableCapabilities(WorkerSnapshot? worker) =>
        EffectiveClaimReady(worker);

    public static bool CapabilityIsClaimable(WorkerSnapshot? worker, string? capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) return false;
        var cap = capability.Trim();
        return ClaimableCapabilities(worker).Any(c => string.Equals(c, cap, StringComparison.OrdinalIgnoreCase));
    }
}
