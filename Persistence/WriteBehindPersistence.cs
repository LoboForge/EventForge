using EventForge.Configuration;
using EventForge.Core;
using EventForge.Queue;
using EventForge.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EventForge.Persistence;

public sealed class WriteBehindPersistence : BackgroundService
{
    private readonly EventForgeOptions _opts;
    private readonly InMemoryJobQueue _jobs;
    private readonly IEventStore _events;
    private readonly ILogger<WriteBehindPersistence> _log;
    private static readonly TimeSpan JobRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan JobPruneInterval = TimeSpan.FromHours(1);

    private volatile bool _loaded;
    private int _pendingWrites;
    private DateTimeOffset _lastJobPruneUtc = DateTimeOffset.MinValue;

    public bool IsLoaded => _loaded;
    public int PendingWrites => Volatile.Read(ref _pendingWrites);

    public WriteBehindPersistence(
        IOptions<EventForgeOptions> options,
        InMemoryJobQueue jobs,
        IEventStore events,
        ILogger<WriteBehindPersistence> log)
    {
        _opts = options.Value;
        _jobs = jobs;
        _events = events;
        _log = log;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        await _events.InitializeAsync(ct);
        var path = Path.GetFullPath(_opts.SqlitePath);
        if (!File.Exists(path))
        {
            _loaded = true;
            return;
        }

        await using var conn = Open(path);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS jobs (
              job_id TEXT PRIMARY KEY,
              app_id TEXT NOT NULL,
              capability TEXT NOT NULL,
              tier TEXT NOT NULL,
              kind TEXT NOT NULL,
              payload_json TEXT NOT NULL,
              status TEXT NOT NULL,
              worker_id TEXT,
              worker_hostname TEXT,
              created_at TEXT NOT NULL,
              leased_until TEXT,
              completed_at TEXT,
              output_url TEXT,
              output_content_type TEXT,
              text_reply TEXT,
              error TEXT
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        await EnsureWorkerHostnameColumnAsync(conn, ct);

        cmd.CommandText = """
            SELECT * FROM jobs
            WHERE status IN ('Queued', 'Leased', 'Streaming')
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var loaded = 0;
        while (await reader.ReadAsync(ct))
        {
            var job = ReadJob(reader);
            if (job.Status is JobStatus.Leased or JobStatus.Streaming)
            {
                job.Status = JobStatus.Queued;
                job.WorkerId = null;
                job.WorkerHostname = null;
                job.LeasedUntil = null;
            }
            _jobs.Load(job);
            loaded++;
        }
        _loaded = true;
        _log.LogInformation("EventForge cache loaded {Count} jobs from SQLite", loaded);
    }


    public async Task<int> DeleteJobsAsync(IReadOnlyCollection<string> jobIds, CancellationToken ct)
    {
        if (jobIds.Count == 0) return 0;
        var idSet = new HashSet<string>(jobIds, StringComparer.OrdinalIgnoreCase);
        var removedMemory = _jobs.RemoveWhere(j => idSet.Contains(j.JobId)).Count;

        var path = Path.GetFullPath(_opts.SqlitePath);
        var removedDb = 0;
        if (File.Exists(path))
        {
            await using var conn = Open(path);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
            foreach (var id in idSet)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM jobs WHERE job_id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                removedDb += await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        MarkDirty();
        _log.LogWarning("EventForge deleted {Memory} in-memory and {Db} SQLite job row(s)", removedMemory, removedDb);
        return removedMemory;
    }


    public void MarkDirty() => Interlocked.Increment(ref _pendingWrites);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opts.FlushIntervalSeconds)), stoppingToken);
            await FlushAsync(stoppingToken);
            if (DateTimeOffset.UtcNow - _lastJobPruneUtc >= JobPruneInterval)
            {
                await PruneOldJobsAsync(stoppingToken);
                _lastJobPruneUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    private async Task PruneOldJobsAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - JobRetention;
        var cutoffIso = cutoff.ToString("O");
        var removedFromMemory = _jobs.PruneTerminalOlderThan(cutoff);

        var path = Path.GetFullPath(_opts.SqlitePath);
        if (!File.Exists(path))
        {
            if (removedFromMemory > 0)
                _log.LogInformation("EventForge job prune: removed {Count} in-memory row(s) older than {Days} days", removedFromMemory, JobRetention.TotalDays);
            return;
        }

        await using var conn = Open(path);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM jobs
            WHERE status IN ($completed, $failed)
              AND COALESCE(completed_at, created_at) < $cutoff
            """;
        cmd.Parameters.AddWithValue("$completed", JobStatus.Completed);
        cmd.Parameters.AddWithValue("$failed", JobStatus.Failed);
        cmd.Parameters.AddWithValue("$cutoff", cutoffIso);
        var removedFromDb = await cmd.ExecuteNonQueryAsync(ct);

        if (removedFromMemory > 0 || removedFromDb > 0)
        {
            _log.LogInformation(
                "EventForge job prune: removed {Memory} in-memory and {Db} SQLite row(s) older than {Days} days",
                removedFromMemory, removedFromDb, JobRetention.TotalDays);
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        var jobs = _jobs.SnapshotJobs();
        var path = Path.GetFullPath(_opts.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = Open(path);
        await conn.OpenAsync(ct);
        var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var create = conn.CreateCommand())
        {
            create.Transaction = (SqliteTransaction)tx;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS jobs (
                  job_id TEXT PRIMARY KEY,
                  app_id TEXT NOT NULL,
                  capability TEXT NOT NULL,
                  tier TEXT NOT NULL,
                  kind TEXT NOT NULL,
                  payload_json TEXT NOT NULL,
                  status TEXT NOT NULL,
                  worker_id TEXT,
                  created_at TEXT NOT NULL,
                  leased_until TEXT,
                  completed_at TEXT,
                  output_url TEXT,
                  output_content_type TEXT,
                  text_reply TEXT,
                  error TEXT
                );
                """;
            await create.ExecuteNonQueryAsync(ct);
        }

        foreach (var job in jobs)
        {
            await using var upsert = conn.CreateCommand();
            upsert.Transaction = (SqliteTransaction)tx;
            upsert.CommandText = """
                INSERT INTO jobs (
                  job_id, app_id, capability, tier, kind, payload_json, status, worker_id, worker_hostname,
                  created_at, leased_until, completed_at, output_url, output_content_type, text_reply, error
                ) VALUES (
                  $id, $app, $cap, $tier, $kind, $payload, $status, $worker, $workerHost,
                  $created, $leased, $completed, $outUrl, $outCt, $text, $error
                ) ON CONFLICT(job_id) DO UPDATE SET
                  status=$status, worker_id=$worker, worker_hostname=$workerHost, leased_until=$leased, completed_at=$completed,
                  output_url=$outUrl, output_content_type=$outCt, text_reply=$text, error=$error
                """;
            BindJob(upsert, job);
            await upsert.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        Interlocked.Exchange(ref _pendingWrites, 0);
    }


    private static async Task EnsureWorkerHostnameColumnAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(jobs)";
        await using var reader = await pragma.ExecuteReaderAsync(ct);
        var hasColumn = false;
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), "worker_hostname", StringComparison.OrdinalIgnoreCase))
            {
                hasColumn = true;
                break;
            }
        }
        if (hasColumn) return;
        await using var alter = conn.CreateCommand();
        alter.CommandText = "ALTER TABLE jobs ADD COLUMN worker_hostname TEXT";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static string? GetString(SqliteDataReader r, string name, int fallbackIndex)
    {
        try
        {
            var ord = r.GetOrdinal(name);
            return r.IsDBNull(ord) ? null : r.GetString(ord);
        }
        catch (IndexOutOfRangeException)
        {
            return r.IsDBNull(fallbackIndex) ? null : r.GetString(fallbackIndex);
        }
    }

    private static string? GetNullableString(SqliteDataReader r, string name, int fallbackIndex) => GetString(r, name, fallbackIndex);

    private static DateTimeOffset? ParseNullableDate(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : DateTimeOffset.Parse(raw);

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);

    private static void BindJob(SqliteCommand cmd, JobRecord job)
    {
        cmd.Parameters.AddWithValue("$id", job.JobId);
        cmd.Parameters.AddWithValue("$app", job.AppId);
        cmd.Parameters.AddWithValue("$cap", job.Capability);
        cmd.Parameters.AddWithValue("$tier", job.Tier);
        cmd.Parameters.AddWithValue("$kind", job.Kind);
        cmd.Parameters.AddWithValue("$payload", job.PayloadJson);
        cmd.Parameters.AddWithValue("$status", job.Status);
        cmd.Parameters.AddWithValue("$worker", (object?)job.WorkerId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$workerHost", (object?)job.WorkerHostname ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$leased", job.LeasedUntil?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$completed", job.CompletedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$outUrl", (object?)job.OutputUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$outCt", (object?)job.OutputContentType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$text", (object?)job.TextReply ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)job.Error ?? DBNull.Value);
    }

    private static JobRecord ReadJob(SqliteDataReader r) => new()
    {
        JobId = r.GetString(0),
        AppId = r.GetString(1),
        Capability = r.GetString(2),
        Tier = r.GetString(3),
        Kind = r.GetString(4),
        PayloadJson = r.GetString(5),
        Status = r.GetString(6),
        WorkerId = GetNullableString(r, "worker_id", 7),
        WorkerHostname = GetNullableString(r, "worker_hostname", 8),
        CreatedAt = DateTimeOffset.Parse(GetString(r, "created_at", 9) ?? r.GetString(8)),
        LeasedUntil = ParseNullableDate(GetString(r, "leased_until", 10) ?? (r.FieldCount > 9 && !r.IsDBNull(9) ? r.GetString(9) : null)),
        CompletedAt = ParseNullableDate(GetString(r, "completed_at", 11) ?? (r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetString(10) : null)),
        OutputUrl = GetNullableString(r, "output_url", 12) ?? (r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetString(11) : null),
        OutputContentType = GetNullableString(r, "output_content_type", 13) ?? (r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetString(12) : null),
        TextReply = GetNullableString(r, "text_reply", 14) ?? (r.FieldCount > 13 && !r.IsDBNull(13) ? r.GetString(13) : null),
        Error = GetNullableString(r, "error", 15) ?? (r.FieldCount > 14 && !r.IsDBNull(14) ? r.GetString(14) : null),
    };
}
