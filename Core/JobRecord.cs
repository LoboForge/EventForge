namespace EventForge.Core;

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Leased = "leased";
    public const string Streaming = "streaming";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class JobKind
{
    public const string Image = "image";
    public const string TextStream = "text_stream";
}

public sealed class JobRecord
{
    public required string JobId { get; init; }
    public required string AppId { get; init; }
    public required string Capability { get; init; }
    public required string Tier { get; init; }
    /// <summary>Optional intra-tier ordering; when null, derived from <see cref="Tier"/>.</summary>
    public int? QueuePriority { get; init; }
    public required string Kind { get; init; }
    public required string PayloadJson { get; init; }
    public string Status { get; set; } = JobStatus.Queued;
    public string? WorkerId { get; set; }
    /// <summary>Display identity from claim body (hostname); auth token maps to WorkerId.</summary>
    public string? WorkerHostname { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LeasedUntil { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? OutputUrl { get; set; }
    public string? OutputContentType { get; set; }
    public string? TextReply { get; set; }
    public string? Error { get; set; }
}
