namespace EventForge.Storage;

public interface IEventStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<EventRecord?> PersistAsync(
        string appId,
        string jobId,
        string eventType,
        string manifestJson,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken ct = default);
    Task<IReadOnlyList<EventRecord>> QuerySinceAsync(
        string appId, DateTimeOffset since, CancellationToken ct = default);
    Task PurgeExpiredAsync(CancellationToken ct = default);
}
