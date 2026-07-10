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
    Task<SqliteS3BackupResult> BackupAsync(CancellationToken ct = default);
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

        var restoreKey = await PickBestRestoreKeyAsync(bucket, key, ct);
        if (restoreKey == null)
        {
            _log.LogInformation("No trusted EventForge SQLite snapshot in s3://{Bucket}/{Prefix}; starting fresh", bucket, key);
            return;
        }

        try
        {
            using var resp = await _s3!.GetObjectAsync(bucket, restoreKey, ct);
            await using var fs = File.Create(DatabasePath);
            await resp.ResponseStream.CopyToAsync(fs, ct);
            var jobs = await SqliteStoreStats.CountJobsAsync(DatabasePath, ct);
            _log.LogInformation(
                "EventForge SQLite restored from s3://{Bucket}/{Key} ({Jobs} jobs, {Bytes} bytes)",
                bucket, restoreKey, jobs, SqliteStoreStats.FileBytes(DatabasePath));
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

    public async Task<SqliteS3BackupResult> BackupAsync(CancellationToken ct = default)
    {
        if (_s3 == null || !File.Exists(DatabasePath))
        {
            return new SqliteS3BackupResult
            {
                Skipped = true,
                SkipReason = "s3_disabled_or_missing_db",
                LocalBytes = SqliteStoreStats.FileBytes(DatabasePath),
            };
        }

        var bucket = _opts.S3.Bucket.Trim();
        var key = _opts.S3.Key.Trim().TrimStart('/');
        if (bucket.Length == 0 || key.Length == 0)
        {
            return new SqliteS3BackupResult { Skipped = true, SkipReason = "s3_not_configured" };
        }

        var localJobs = await SqliteStoreStats.CountJobsAsync(DatabasePath, ct);
        var localBytes = SqliteStoreStats.FileBytes(DatabasePath);
        var (remoteBytes, remoteJobs) = await InspectRemoteAsync(bucket, key, ct);

        if (SqliteBackupPolicy.ShouldRefuseOverwrite(localJobs, localBytes, remoteJobs, remoteBytes))
        {
            var reason =
                $"local_jobs={localJobs} local_bytes={localBytes} remote_jobs={remoteJobs} remote_bytes={remoteBytes}";
            _log.LogWarning("EventForge refusing S3 backup upload — snapshot looks like a destructive shrink ({Reason})", reason);
            return new SqliteS3BackupResult
            {
                Skipped = true,
                SkipReason = reason,
                LocalJobCount = localJobs,
                RemoteJobCount = remoteJobs,
                LocalBytes = localBytes,
                RemoteBytes = remoteBytes,
            };
        }

        var tempPath = DatabasePath + ".backup";
        string? datedKey = null;
        try
        {
            await using (var source = new SqliteConnection($"Data Source={DatabasePath}"))
            {
                await source.OpenAsync(ct);
                await using var dest = new SqliteConnection($"Data Source={tempPath}");
                await dest.OpenAsync(ct);
                source.BackupDatabase(dest);
            }

            if (remoteBytes > 0)
                datedKey = await CopyRemoteToDatedBackupAsync(bucket, key, ct);

            await using var uploadStream = File.OpenRead(tempPath);
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                InputStream = uploadStream,
                ContentType = "application/x-sqlite3",
            }, ct);

            _log.LogInformation(
                "EventForge SQLite backed up to s3://{Bucket}/{Key} ({Jobs} jobs, {Bytes} bytes)",
                bucket, key, localJobs, localBytes);

            return new SqliteS3BackupResult
            {
                Uploaded = true,
                LocalJobCount = localJobs,
                RemoteJobCount = remoteJobs,
                LocalBytes = localBytes,
                RemoteBytes = remoteBytes,
                DatedBackupKey = datedKey,
            };
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventForge S3 backup failed");
            return new SqliteS3BackupResult
            {
                Skipped = true,
                SkipReason = ex.Message,
                LocalJobCount = localJobs,
                RemoteJobCount = remoteJobs,
                LocalBytes = localBytes,
                RemoteBytes = remoteBytes,
            };
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }

    private async Task<string?> PickBestRestoreKeyAsync(string bucket, string primaryKey, CancellationToken ct)
    {
        var prefix = primaryKey.Contains('/') ? primaryKey[..(primaryKey.LastIndexOf('/') + 1)] : "";
        var candidates = new List<(string Key, long Size)>();

        try
        {
            string? token = null;
            do
            {
                var resp = await _s3!.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = bucket,
                    Prefix = prefix + "store.db",
                    ContinuationToken = token,
                }, ct);
                foreach (var obj in resp.S3Objects)
                {
                    if (obj.Key is { Length: > 0 } k && obj.Size > 0)
                        candidates.Add((k, obj.Size));
                }
                token = resp.IsTruncated ? resp.NextContinuationToken : null;
            } while (token != null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventForge S3 list for restore candidates failed; falling back to primary key");
            candidates.Add((primaryKey, 0));
        }

        if (candidates.Count == 0)
            return null;

        foreach (var (candidateKey, size) in candidates.OrderByDescending(c => c.Size))
        {
            if (size > 0 && !SqliteBackupPolicy.IsTrustedSnapshot(0, size))
                continue;

            if (size == 0)
            {
                var jobs = await InspectRemoteJobsAsync(bucket, candidateKey, ct);
                if (!SqliteBackupPolicy.IsTrustedSnapshot(jobs, 0))
                    continue;
            }

            if (!string.Equals(candidateKey, primaryKey, StringComparison.Ordinal))
            {
                _log.LogWarning(
                    "EventForge primary S3 snapshot untrusted; restoring from s3://{Bucket}/{Key} ({Bytes} bytes) instead",
                    bucket, candidateKey, size);
            }
            return candidateKey;
        }

        return null;
    }

    private async Task<(long Bytes, int Jobs)> InspectRemoteAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var head = await _s3!.GetObjectMetadataAsync(bucket, key, ct);
            var bytes = head.ContentLength;
            var jobs = bytes >= SqliteBackupPolicy.MinTrustedBytes
                ? await InspectRemoteJobsAsync(bucket, key, ct)
                : 0;
            return (bytes, jobs);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return (0, 0);
        }
    }

    private async Task<int> InspectRemoteJobsAsync(string bucket, string key, CancellationToken ct)
    {
        var temp = Path.GetTempFileName();
        try
        {
            using var resp = await _s3!.GetObjectAsync(bucket, key, ct);
            await using var fs = File.Create(temp);
            await resp.ResponseStream.CopyToAsync(fs, ct);
            return await SqliteStoreStats.CountJobsAsync(temp, ct);
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
    }

    private async Task<string?> CopyRemoteToDatedBackupAsync(string bucket, string key, CancellationToken ct)
    {
        try
        {
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var datedKey = key + ".backup-" + stamp;
            await _s3!.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = key,
                DestinationBucket = bucket,
                DestinationKey = datedKey,
            }, ct);
            _log.LogInformation("EventForge copied s3://{Bucket}/{Source} → {Dest} before upload", bucket, key, datedKey);
            return datedKey;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventForge dated S3 backup copy failed (continuing with upload)");
            return null;
        }
    }

    public void Dispose() => _s3?.Dispose();
}
