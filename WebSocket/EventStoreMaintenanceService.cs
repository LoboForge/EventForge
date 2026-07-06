using EventForge.Storage;

namespace EventForge.WebSocket;

public sealed class EventStoreMaintenanceService : BackgroundService
{
    private readonly IEventStore _store;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public EventStoreMaintenanceService(IEventStore store) => _store = store;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _store.PurgeExpiredAsync(stoppingToken);
            await Task.Delay(_interval, stoppingToken);
        }
    }
}
