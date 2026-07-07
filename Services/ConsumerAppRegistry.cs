using System.Collections.Concurrent;

namespace EventForge.Services;

/// <summary>Per-consumer app state (pause when out of generations, etc.).</summary>
public sealed class ConsumerAppRegistry
{
    private readonly ConcurrentDictionary<string, AppState> _apps = new(StringComparer.OrdinalIgnoreCase);

    public bool IsPaused(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        return _apps.TryGetValue(appId.Trim(), out var s) && s.Paused;
    }

    public AppState? Get(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        return _apps.TryGetValue(appId.Trim(), out var s) ? s : null;
    }

    public IReadOnlyList<AppState> ListAll() => _apps.Values.OrderBy(s => s.AppId).ToList();

    public AppState Pause(string appId, string? reason = null, string? pausedBy = null)
    {
        var id = appId.Trim();
        return _apps.AddOrUpdate(
            id,
            _ => new AppState
            {
                AppId = id,
                Paused = true,
                PauseReason = reason ?? "generations_exhausted",
                PausedAtUtc = DateTimeOffset.UtcNow,
                PausedBy = pausedBy,
            },
            (_, existing) =>
            {
                existing.Paused = true;
                existing.PauseReason = reason ?? existing.PauseReason ?? "generations_exhausted";
                existing.PausedAtUtc = DateTimeOffset.UtcNow;
                existing.PausedBy = pausedBy ?? existing.PausedBy;
                return existing;
            });
    }

    public AppState? Unpause(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return null;
        var id = appId.Trim();
        if (!_apps.TryGetValue(id, out var s)) return null;
        s.Paused = false;
        s.PauseReason = null;
        s.PausedAtUtc = null;
        s.PausedBy = null;
        s.UnpausedAtUtc = DateTimeOffset.UtcNow;
        return s;
    }
}

public sealed class AppState
{
    public required string AppId { get; init; }
    public bool Paused { get; set; }
    public string? PauseReason { get; set; }
    public DateTimeOffset? PausedAtUtc { get; set; }
    public DateTimeOffset? UnpausedAtUtc { get; set; }
    public string? PausedBy { get; set; }
}
