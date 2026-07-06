using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using EventForge.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace EventForge.Storage;

/// <summary>SQLite event store backup/restore via S3.</summary>
public interface ISqliteS3Persistence
{
    string DatabasePath { get; }
    Task RestoreOnStartupAsync(CancellationToken ct = default);
    Task BackupAsync(CancellationToken ct = default);
}

public sealed class SqliteS3Persistence : ISqliteS3Persistence, IDisposable
{
    private readonly EventForgeOptions _opts;
    private readonly ILogger<SqliteS3Persistence> _log;
    private readonly IAmazonS3? _s3;

    public SqliteS3Persistence(IOptions<EventForgeOptions> options, ILogger<SqliteS3Persistence> log)
    {
        _opts = options.Value;
        _log = log;
        DatabasePath = Path.GetFullPath(_opts.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        if (_opts.S3.Enabled && !string.IsNullOrWhiteSpace(_opts.S3.Bucket))
        {
            var region = string.IsNullOrWhiteSpace(_opts.S3.Region) ? _opts.AwsRegion : _opts.S3.Region;
            _s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(region.Trim()));
        }
    }

    public string DatabasePath { get; }

    public async Task RestoreOnStartupAsync(CancellationToken ct = default)
    {
        if (_s3 == null || !_opts.S3.LoadOnStartup) return;

        var bucket = _opts.S3.Bucket.Trim();
        var key = _opts.S3.Key.Trim().TrimStart('/');
        if (bucket.Length == 0 || key.Length == 0) return;

        if (File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length > 0)
        {
            _log.LogInformation("EventForge SQLite exists locally at {Path}; skipping S3 restore", DatabasePath);
            return;
        }

        try
        {
            using var resp = await _s3.GetObjectAsync(bucket, key, ct);
            await using var fs = File.Create(DatabasePath);
            await resp.ResponseStream.CopyToAsync(fs, ct);
            _log.LogInformation("EventForge SQLite restored from s3://{Bucket}/{Key}", bucket, key);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _log.LogInformation("No EventForge SQLite in s3://{Bucket}/{Key}; starting fresh", bucket, key);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventForge S3 restore failed; starting with local SQLite");
        }
    }

    public async Task BackupAsync(CancellationToken ct = default)
    {
        if (_s3 == null) return;
        if (!File.Exists(DatabasePath)) return;

        var bucket = _opts.S3.Bucket.Trim();
        var key = _opts.S3.Key.Trim().TrimStart('/');
        if (bucket.Length == 0 || key.Length == 0) return;

        var tempPath = DatabasePath + ".backup";
        try
        {
            await using (var source = new SqliteConnection($"Data Source={DatabasePath}"))
            {
                await source.OpenAsync(ct);
                await using var dest = new SqliteConnection($"Data Source={tempPath}");
                await dest.OpenAsync(ct);
                source.BackupDatabase(dest);
            }

            await using var uploadStream = File.OpenRead(tempPath);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = uploadStream,
                ContentType = "application/x-sqlite3",
            }, ct);
            _log.LogDebug("EventForge SQLite backed up to s3://{Bucket}/{Key}", bucket, key);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventForge S3 backup failed");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }

    public void Dispose() => _s3?.Dispose();
}
