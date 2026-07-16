using System.Collections.Concurrent;
using EventForge.Configuration;
using EventForge.Models;
using EventForge.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EventForge.Services;

/// <summary>SQLite-backed catalog of app-scoped LoRA assets with an in-memory ready index.</summary>
public sealed class LoraAssetCatalog
{
    private readonly EventForgeOptions _opts;
    private readonly ILogger<LoraAssetCatalog> _log;
    private readonly ConcurrentDictionary<string, LoraAssetRecord> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _readyByAppFile = new(StringComparer.OrdinalIgnoreCase);
    private int _initialized;

    public LoraAssetCatalog(IOptions<EventForgeOptions> options, ILogger<LoraAssetCatalog> log)
    {
        _opts = options.Value;
        _log = log;
    }

    public Task InitializeAsync(CancellationToken ct = default) => ReloadAsync(ct);

    /// <summary>Load (or reload after SQLite S3 restore) the in-memory index from disk.</summary>
    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var path = Path.GetFullPath(_opts.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var conn = SqliteConnectionFactory.Open(path);
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT asset_id, app_id, file_name, object_key, content_type, bytes, sha256, modes, status, created_at, completed_at
            FROM lora_assets
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var loaded = new Dictionary<string, LoraAssetRecord>(StringComparer.OrdinalIgnoreCase);
        var ready = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            var record = ReadRecord(reader);
            loaded[record.AssetId] = record;
            if (string.Equals(record.Status, LoraAssetStatus.Ready, StringComparison.OrdinalIgnoreCase))
                ready[ReadyKey(record.AppId, record.FileName)] = record.AssetId;
        }

        _byId.Clear();
        foreach (var (k, v) in loaded) _byId[k] = v;
        _readyByAppFile.Clear();
        foreach (var (k, v) in ready) _readyByAppFile[k] = v;
        Volatile.Write(ref _initialized, 1);
        _log.LogInformation("LoRA asset catalog loaded {Count} row(s)", loaded.Count);
    }

    public IReadOnlyList<LoraAssetRecord> ListForApp(string appId, string? status = null)
    {
        var app = appId.Trim();
        return _byId.Values
            .Where(r => string.Equals(r.AppId, app, StringComparison.OrdinalIgnoreCase))
            .Where(r => status == null || string.Equals(r.Status, status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.CreatedAt)
            .ToList();
    }

    public LoraAssetRecord? TryGet(string assetId) =>
        _byId.TryGetValue(assetId.Trim(), out var r) ? r : null;

    public LoraAssetRecord? TryGetReadyByAppFile(string appId, string fileName)
    {
        var key = ReadyKey(appId, fileName);
        if (!_readyByAppFile.TryGetValue(key, out var assetId)) return null;
        return TryGet(assetId) is { Status: LoraAssetStatus.Ready } ready ? ready : null;
    }

    public bool IsReadyForApp(string appId, string fileName) =>
        TryGetReadyByAppFile(appId, fileName) != null;

    public bool HasAllReadyForApp(string appId, IEnumerable<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!IsReadyForApp(appId, name)) return false;
        }
        return true;
    }

    public async Task UpsertAsync(LoraAssetRecord record, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var path = Path.GetFullPath(_opts.SqlitePath);
        await using var conn = SqliteConnectionFactory.Open(path);
        await conn.OpenAsync(ct);
        await EnsureTableAsync(conn, ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO lora_assets (
              asset_id, app_id, file_name, object_key, content_type, bytes, sha256, modes, status, created_at, completed_at
            ) VALUES (
              $id, $app, $file, $key, $ct, $bytes, $sha, $modes, $status, $created, $completed
            ) ON CONFLICT(asset_id) DO UPDATE SET
              file_name=$file, object_key=$key, content_type=$ct, bytes=$bytes, sha256=$sha,
              modes=$modes, status=$status, completed_at=$completed
            """;
        Bind(cmd, record);
        await cmd.ExecuteNonQueryAsync(ct);

        if (_byId.TryGetValue(record.AssetId, out var prev)
            && string.Equals(prev.Status, LoraAssetStatus.Ready, StringComparison.OrdinalIgnoreCase))
        {
            _readyByAppFile.TryRemove(ReadyKey(prev.AppId, prev.FileName), out _);
        }
        _byId[record.AssetId] = record;
        IndexReady(record);
    }

    public async Task DeleteAsync(string assetId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        var id = assetId.Trim();
        if (_byId.TryRemove(id, out var prev)
            && string.Equals(prev.Status, LoraAssetStatus.Ready, StringComparison.OrdinalIgnoreCase))
        {
            _readyByAppFile.TryRemove(ReadyKey(prev.AppId, prev.FileName), out _);
        }

        var path = Path.GetFullPath(_opts.SqlitePath);
        await using var conn = SqliteConnectionFactory.Open(path);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM lora_assets WHERE asset_id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void IndexReady(LoraAssetRecord record)
    {
        if (!string.Equals(record.Status, LoraAssetStatus.Ready, StringComparison.OrdinalIgnoreCase))
            return;
        _readyByAppFile[ReadyKey(record.AppId, record.FileName)] = record.AssetId;
    }

    private static string ReadyKey(string appId, string fileName)
    {
        var baseName = Path.GetFileName((fileName ?? "").Trim().Replace('\\', '/'));
        return $"{appId.Trim().ToLowerInvariant()}\0{baseName.ToLowerInvariant()}";
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized) == 1) return;
        await ReloadAsync(ct);
    }

    private static async Task EnsureTableAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS lora_assets (
              asset_id TEXT PRIMARY KEY,
              app_id TEXT NOT NULL,
              file_name TEXT NOT NULL,
              object_key TEXT NOT NULL,
              content_type TEXT NOT NULL,
              bytes INTEGER,
              sha256 TEXT,
              modes TEXT NOT NULL DEFAULT 'all',
              status TEXT NOT NULL,
              created_at TEXT NOT NULL,
              completed_at TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ix_lora_assets_app_file
              ON lora_assets(app_id, file_name);
            CREATE INDEX IF NOT EXISTS ix_lora_assets_app_status
              ON lora_assets(app_id, status);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand cmd, LoraAssetRecord r)
    {
        cmd.Parameters.AddWithValue("$id", r.AssetId);
        cmd.Parameters.AddWithValue("$app", r.AppId);
        cmd.Parameters.AddWithValue("$file", r.FileName);
        cmd.Parameters.AddWithValue("$key", r.ObjectKey);
        cmd.Parameters.AddWithValue("$ct", r.ContentType);
        cmd.Parameters.AddWithValue("$bytes", r.Bytes.HasValue ? r.Bytes.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$sha", (object?)r.Sha256 ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$modes", r.Modes);
        cmd.Parameters.AddWithValue("$status", r.Status);
        cmd.Parameters.AddWithValue("$created", r.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completed", r.CompletedAt.HasValue ? r.CompletedAt.Value.ToString("O") : DBNull.Value);
    }

    private static LoraAssetRecord ReadRecord(SqliteDataReader r) => new()
    {
        AssetId = r.GetString(0),
        AppId = r.GetString(1),
        FileName = r.GetString(2),
        ObjectKey = r.GetString(3),
        ContentType = r.GetString(4),
        Bytes = r.IsDBNull(5) ? null : r.GetInt64(5),
        Sha256 = r.IsDBNull(6) ? null : r.GetString(6),
        Modes = r.GetString(7),
        Status = r.GetString(8),
        CreatedAt = DateTimeOffset.Parse(r.GetString(9)),
        CompletedAt = r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10)),
    };
}
