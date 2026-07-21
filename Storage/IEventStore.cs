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

    /// <summary>
    /// Delete all persisted events for a job so a consumer replaying <c>?since=</c> can no longer
    /// receive a moderated/deleted output. Returns the number of event rows removed.
    /// </summary>
    Task<int> DeleteByJobAsync(string jobId, CancellationToken ct = default);
}
