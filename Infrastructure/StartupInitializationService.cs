using EventForge.Persistence;
using EventForge.Storage;

namespace EventForge.Infrastructure;

/// <summary>
/// Restores SQLite from S3 and hydrates the in-memory job cache without blocking Kestrel startup.
/// </summary>
public sealed class StartupInitializationService : IHostedService
{
    private readonly ISqliteS3Persistence _sqliteS3;
    private readonly WriteBehindPersistence _persist;
    private readonly IEventStore _events;
    private readonly ILogger<StartupInitializationService> _log;
    private Task? _initializeTask;

    public StartupInitializationService(
        ISqliteS3Persistence sqliteS3,
        WriteBehindPersistence persist,
        IEventStore events,
        ILogger<StartupInitializationService> log)
    {
        _sqliteS3 = sqliteS3;
        _persist = persist;
        _events = events;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Fast schema setup only — keeps hosted services from failing before hydration.
        await _events.InitializeAsync(cancellationToken);
        _initializeTask = InitializeInBackgroundAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_initializeTask == null) return;
        try
        {
            await _initializeTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown while init still running.
        }
    }

    internal async Task InitializeInBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _sqliteS3.RestoreOnStartupAsync(cancellationToken);
            await _persist.LoadAsync(cancellationToken);
            _log.LogInformation("EventForge startup initialization completed (cache_loaded={Loaded})", _persist.IsLoaded);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogCritical(ex, "EventForge startup initialization failed");
        }
    }
}
