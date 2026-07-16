namespace EventForge.Models;

public static class LoraAssetStatus
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";
}

public sealed class LoraAssetRecord
{
    public required string AssetId { get; init; }
    public required string AppId { get; init; }
    public required string FileName { get; init; }
    public required string ObjectKey { get; init; }
    public required string ContentType { get; init; }
    public long? Bytes { get; set; }
    public string? Sha256 { get; set; }
    /// <summary>Comma-separated modes: image, video, all.</summary>
    public required string Modes { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
