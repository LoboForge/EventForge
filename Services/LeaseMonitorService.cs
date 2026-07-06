namespace EventForge.Services;

/// <summary>Detects expired leases and emits timeout events (jobs are requeued in-memory).</summary>
public sealed class LeaseMonitorService : BackgroundService
{
    private readonly JobService _jobs;
    private readonly ILogger<LeaseMonitorService> _log;

    public LeaseMonitorService(JobService jobs, ILogger<LeaseMonitorService> log)
    {
        _jobs = jobs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var n = await _jobs.ProcessExpiredLeasesAsync(stoppingToken);
                if (n > 0)
                    _log.LogWarning("EventForge requeued {Count} expired lease(s)", n);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Lease monitor cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }
}
