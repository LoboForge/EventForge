using Microsoft.Data.Sqlite;

namespace EventForge.Storage;

public static class SqliteStoreStats
{
    public static async Task<int> CountJobsAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return 0;
        await using var conn = Open(path);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM jobs";
        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long n ? (int)n : Convert.ToInt32(result ?? 0);
        }
        catch (SqliteException)
        {
            return 0;
        }
    }

    public static long FileBytes(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static SqliteConnection Open(string path) =>
        new(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
}
