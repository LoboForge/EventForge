using Microsoft.Data.Sqlite;

namespace EventForge.Storage;

/// <summary>
/// Shared SQLite open helpers. Backup/restore must use Pooling=false and avoid writing
/// snapshot files next to the live DB — Microsoft.Data.Sqlite pooling + delete of a
/// sibling <c>*.backup</c> file triggers SQLITE_READONLY_DBMOVED (1032) on the next
/// BackupDatabase attempt (pre-deploy flush-backup gate failure).
/// </summary>
internal static class SqliteConnectionFactory
{
    public static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, bool pooling = true) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = pooling,
        }.ConnectionString);

    /// <summary>Unpooled read-write connection for online backup / checkpoint.</summary>
    public static SqliteConnection OpenUnpooled(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate) =>
        Open(path, mode, pooling: false);

    public static void DeleteSidecarFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var sidecar = databasePath + suffix;
            if (!File.Exists(sidecar)) continue;
            try { File.Delete(sidecar); }
            catch { /* ignore — best effort before replace */ }
        }
    }

    public static void ReplaceDatabaseFile(string sourcePath, string destinationPath)
    {
        SqliteConnection.ClearAllPools();
        DeleteSidecarFiles(destinationPath);
        File.Copy(sourcePath, destinationPath, overwrite: true);
        DeleteSidecarFiles(destinationPath);
        SqliteConnection.ClearAllPools();
    }
}
