using EventForge.Storage;

namespace EventForge.Tests.Support;

internal sealed class DelayedSqliteS3Persistence : ISqliteS3Persistence
{
    private readonly TimeSpan _delay;

    public DelayedSqliteS3Persistence(TimeSpan delay, string? databasePath = null)
    {
        _delay = delay;
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), $"ef-delay-{Guid.NewGuid():N}.db");
    }

    public string DatabasePath { get; }

    public async Task RestoreOnStartupAsync(CancellationToken ct = default)
    {
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, ct);
    }

    public Task<SqliteS3RestoreResult> ForceRestoreFromS3Async(CancellationToken ct = default) =>
        Task.FromResult(new SqliteS3RestoreResult { Skipped = true, SkipReason = "test_double" });

    public Task<SqliteS3BackupResult> BackupAsync(CancellationToken ct = default) =>
        Task.FromResult(new SqliteS3BackupResult { Skipped = true, SkipReason = "test_double" });
}
