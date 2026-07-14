using EventForge.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EventForge.Tests.Storage;

public sealed class SqliteBackupSnapshotTests
{
    [Fact]
    public async Task CreateConsistentSnapshot_copies_jobs_under_concurrent_writers()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ef-snap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "event-forge.db");
        var dest = Path.Combine(Path.GetTempPath(), $"ef-snap-out-{Guid.NewGuid():N}.db");

        try
        {
            await using (var conn = SqliteConnectionFactory.OpenUnpooled(source))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE jobs (
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
                await cmd.ExecuteNonQueryAsync();
                for (var i = 0; i < 50; i++)
                {
                    cmd.CommandText =
                        "INSERT INTO jobs(job_id,app_id,capability,tier,kind,payload_json,status,created_at) " +
                        $"VALUES('j{i}','app','image','standard','x','{{}}','queued','2026-01-01T00:00:00Z')";
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // Poison the old sibling path the way production used to: open pooled dest, delete while pooled.
            var sibling = source + ".backup";
            await using (var poison = new SqliteConnection($"Data Source={sibling}"))
            {
                await poison.OpenAsync();
            }
            File.Delete(sibling);

            // Live writers keep using pooled connections on the source.
            await using (var writer = new SqliteConnection($"Data Source={source}"))
            {
                await writer.OpenAsync();
                await using var upsert = writer.CreateCommand();
                upsert.CommandText =
                    "INSERT INTO jobs(job_id,app_id,capability,tier,kind,payload_json,status,created_at) " +
                    "VALUES('live','app','image','standard','x','{}','queued','2026-01-01T00:00:00Z')";
                await upsert.ExecuteNonQueryAsync();
            }

            await SqliteS3Persistence.CreateConsistentSnapshotAsync(source, dest, CancellationToken.None);

            var count = await SqliteStoreStats.CountJobsAsync(dest);
            Assert.True(count >= 50, $"expected >=50 jobs in snapshot, got {count}");

            // Repeated snapshots must keep working (no pool poison on dest).
            await SqliteS3Persistence.CreateConsistentSnapshotAsync(source, dest, CancellationToken.None);
            count = await SqliteStoreStats.CountJobsAsync(dest);
            Assert.True(count >= 50);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            try { File.Delete(dest); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task CreateConsistentSnapshot_survives_readonly_sibling_destination()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ef-ro-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "event-forge.db");
        var sibling = source + ".backup";
        var dest = Path.Combine(Path.GetTempPath(), $"ef-ro-out-{Guid.NewGuid():N}.db");

        try
        {
            await using (var conn = SqliteConnectionFactory.OpenUnpooled(source))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "CREATE TABLE jobs(job_id TEXT PRIMARY KEY); INSERT INTO jobs VALUES('a');";
                await cmd.ExecuteNonQueryAsync();
            }

            await File.WriteAllTextAsync(sibling, "not-a-db");
            File.SetAttributes(sibling, FileAttributes.ReadOnly);

            // Old approach would fail; new path writes under /tmp-style unique dest.
            await SqliteS3Persistence.CreateConsistentSnapshotAsync(source, dest, CancellationToken.None);
            Assert.Equal(1, await SqliteStoreStats.CountJobsAsync(dest));
        }
        finally
        {
            try { File.SetAttributes(sibling, FileAttributes.Normal); } catch { /* ignore */ }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            try { File.Delete(dest); } catch { /* ignore */ }
        }
    }
}
