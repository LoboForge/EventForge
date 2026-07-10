namespace EventForge.Storage;

public sealed class SqliteS3BackupResult
{
    public bool Uploaded { get; init; }
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public int LocalJobCount { get; init; }
    public int RemoteJobCount { get; init; }
    public long LocalBytes { get; init; }
    public long RemoteBytes { get; init; }
    public string? DatedBackupKey { get; init; }
}
