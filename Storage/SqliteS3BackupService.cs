using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Storage;

/// <summary>Periodically uploads the SQLite event store to S3.</summary>
public sealed class SqliteS3BackupService : BackgroundService
{
    private readonly ISqliteS3Persistence _persistence;
    private readonly bool _enabled;
    private readonly TimeSpan _interval;

    public SqliteS3BackupService(ISqliteS3Persistence persistence, IOptions<EventForgeOptions> options)
    {
        _persistence = persistence;
        _enabled = options.Value.S3.Enabled && !string.IsNullOrWhiteSpace(options.Value.S3.Bucket);
        var minutes = Math.Max(1, options.Value.S3.BackupIntervalMinutes);
        _interval = TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        // Initial delay so startup restore + schema init finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await _persistence.BackupAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }
}
