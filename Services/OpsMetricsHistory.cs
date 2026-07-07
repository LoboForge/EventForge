namespace EventForge.Services;

/// <summary>Rolling time-series for ops/consumer dashboard charts (in-memory).</summary>
public sealed class OpsMetricsHistory
{
    private readonly object _lock = new();
    private readonly Queue<MetricsSample> _samples = new();
    private const int MaxSamples = 120;

    public void Record(MetricsSample sample)
    {
        lock (_lock)
        {
            _samples.Enqueue(sample);
            while (_samples.Count > MaxSamples) _samples.Dequeue();
        }
    }

    public IReadOnlyList<MetricsSample> GetRecent(int limit = 60)
    {
        lock (_lock)
        {
            return _samples.TakeLast(Math.Clamp(limit, 1, MaxSamples)).ToList();
        }
    }
}

public sealed class MetricsSample
{
    public required DateTimeOffset AtUtc { get; init; }
    public int JobsQueued { get; init; }
    public int JobsInProgress { get; init; }
    public int JobsFailed { get; init; }
    public int WorkersTotal { get; init; }
    public int WorkersBusy { get; init; }
    public int WorkersStale { get; init; }
    public int WorkersNonContributing { get; init; }
}
