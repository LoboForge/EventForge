namespace EventForge.Storage;

public sealed class SqliteS3RestoreResult
{
    public bool Restored { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public string? RestoreKey { get; init; }
    public int RestoredJobCount { get; init; }
    public long RestoredBytes { get; init; }
    public int ReplacedLocalJobCount { get; init; }
    public long ReplacedLocalBytes { get; init; }
}
