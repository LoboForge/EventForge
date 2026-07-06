namespace EventForge.Storage;

public sealed record EventRecord(
    string EventId,
    string AppId,
    string JobId,
    string EventType,
    string ManifestJson,
    DateTimeOffset CompletedAt,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);
