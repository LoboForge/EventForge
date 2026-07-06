using System.Text.Json;
using EventForge.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EventForge.Storage;

public sealed class SqliteEventStore : IEventStore
{
    private readonly string _connectionString;
    private readonly int _retentionDays;

    public SqliteEventStore(IOptions<EventForgeOptions> options)
    {
        var path = Path.GetFullPath(options.Value.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString;
        _retentionDays = Math.Max(1, options.Value.EventRetentionDays);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS events (
              event_id TEXT PRIMARY KEY,
              app_id TEXT NOT NULL,
              job_id TEXT NOT NULL,
              event_type TEXT NOT NULL,
              manifest_json TEXT NOT NULL,
              completed_at TEXT NOT NULL,
              error TEXT,
              created_at TEXT NOT NULL,
              expires_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_events_app_completed
              ON events(app_id, completed_at);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_events_app_job_type
              ON events(app_id, job_id, event_type);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EventRecord?> PersistAsync(
        string appId,
        string jobId,
        string eventType,
        string manifestJson,
        DateTimeOffset completedAt,
        string? error,
        CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(_retentionDays);
        var eventId = Guid.NewGuid().ToString();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var existing = conn.CreateCommand())
        {
            existing.CommandText = """
                SELECT event_id, app_id, job_id, event_type, manifest_json,
                       completed_at, error, created_at, expires_at
                FROM events
                WHERE app_id = $app AND job_id = $job AND event_type = $type
                LIMIT 1
                """;
            existing.Parameters.AddWithValue("$app", appId);
            existing.Parameters.AddWithValue("$job", jobId);
            existing.Parameters.AddWithValue("$type", eventType);
            await using var reader = await existing.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                return ReadRow(reader);
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO events (
              event_id, app_id, job_id, event_type, manifest_json,
              completed_at, error, created_at, expires_at
            ) VALUES (
              $id, $app, $job, $type, $manifest, $completed, $error, $created, $expires
            )
            """;
        insert.Parameters.AddWithValue("$id", eventId);
        insert.Parameters.AddWithValue("$app", appId);
        insert.Parameters.AddWithValue("$job", jobId);
        insert.Parameters.AddWithValue("$type", eventType);
        insert.Parameters.AddWithValue("$manifest", manifestJson);
        insert.Parameters.AddWithValue("$completed", completedAt.ToString("O"));
        insert.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        insert.Parameters.AddWithValue("$created", now.ToString("O"));
        insert.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);

        return new EventRecord(
            eventId, appId, jobId, eventType, manifestJson,
            completedAt, error, now, expiresAt);
    }

    public async Task<IReadOnlyList<EventRecord>> QuerySinceAsync(
        string appId, DateTimeOffset since, CancellationToken ct = default)
    {
        var results = new List<EventRecord>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT event_id, app_id, job_id, event_type, manifest_json,
                   completed_at, error, created_at, expires_at
            FROM events
            WHERE app_id = $app AND completed_at >= $since
            ORDER BY completed_at ASC
            """;
        cmd.Parameters.AddWithValue("$app", appId);
        cmd.Parameters.AddWithValue("$since", since.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadRow(reader));
        return results;
    }

    public async Task PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE expires_at < $now";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static EventRecord ReadRow(SqliteDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7)),
            DateTimeOffset.Parse(reader.GetString(8)));
}
