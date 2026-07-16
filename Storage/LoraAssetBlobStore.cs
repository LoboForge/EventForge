using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Storage;

public sealed class LoraAssetBlobStore : ILoraAssetBlobStore, IDisposable
{
    private readonly EventForgeOptions _opts;
    private readonly IAmazonS3? _s3;
    private readonly ILogger<LoraAssetBlobStore> _log;

    public LoraAssetBlobStore(IOptions<EventForgeOptions> options, ILogger<LoraAssetBlobStore> log)
    {
        _opts = options.Value;
        _log = log;
        var cfg = _opts.LoraAssets;
        // When Enabled, use own bucket or inherit Artifacts bucket.
        var bucket = FirstNonEmpty(cfg.Bucket, _opts.Artifacts.Enabled ? _opts.Artifacts.Bucket : "");
        if (cfg.Enabled && !string.IsNullOrWhiteSpace(bucket))
        {
            var region = FirstNonEmpty(cfg.Region, _opts.Artifacts.Region, _opts.AwsRegion);
            _s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(region.Trim()));
        }
    }

    public bool S3Enabled => _s3 != null;

    private string Bucket =>
        FirstNonEmpty(_opts.LoraAssets.Bucket, _opts.Artifacts.Bucket).Trim();

    private string Prefix =>
        string.IsNullOrWhiteSpace(_opts.LoraAssets.Prefix)
            ? "event-forge/loras"
            : _opts.LoraAssets.Prefix.Trim().TrimEnd('/');

    public string ObjectKey(string appId, string assetId, string fileName)
    {
        var safeApp = SanitizePathSegment(appId);
        var safeAsset = SanitizePathSegment(assetId);
        var safeName = Path.GetFileName(fileName);
        return $"{Prefix}/{safeApp}/{safeAsset}/{safeName}";
    }

    public Task<string?> CreatePresignedPutUrlAsync(
        string objectKey, string contentType, TimeSpan ttl, CancellationToken ct)
    {
        if (_s3 == null) return Task.FromResult<string?>(null);
        var expires = DateTime.UtcNow.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromHours(1) : ttl);
        var url = _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = Bucket,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Expires = expires,
            ContentType = string.IsNullOrWhiteSpace(contentType)
                ? "application/octet-stream"
                : contentType,
        });
        return Task.FromResult<string?>(url);
    }

    public async Task PutAsync(string objectKey, string contentType, Stream body, CancellationToken ct)
    {
        if (_s3 != null)
        {
            // TransferUtility streams request bodies in multipart-sized chunks. Do not materialize
            // a multi-gigabyte LoRA in memory when the proxy endpoint is used.
            using var transfer = new TransferUtility(_s3);
            var request = new TransferUtilityUploadRequest
            {
                BucketName = Bucket,
                Key = objectKey,
                InputStream = body,
                AutoCloseStream = false,
                ContentType = string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType,
            };
            await transfer.UploadAsync(request, ct);
            return;
        }

        var path = LocalPath(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        await body.CopyToAsync(fs, 1024 * 1024, ct);
    }

    public async Task<(Stream Stream, string ContentType, long Length)?> TryOpenAsync(
        string objectKey, CancellationToken ct)
    {
        if (_s3 != null)
        {
            try
            {
                var resp = await _s3.GetObjectAsync(Bucket, objectKey, ct);
                return (resp.ResponseStream, resp.Headers.ContentType ?? "application/octet-stream", resp.ContentLength);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        var path = LocalPath(objectKey);
        if (!File.Exists(path)) return null;
        var fs = File.OpenRead(path);
        return (fs, "application/octet-stream", new FileInfo(path).Length);
    }

    public async Task<long?> TryGetSizeAsync(string objectKey, CancellationToken ct)
    {
        if (_s3 != null)
        {
            try
            {
                var resp = await _s3.GetObjectMetadataAsync(Bucket, objectKey, ct);
                return resp.ContentLength;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        var path = LocalPath(objectKey);
        return File.Exists(path) ? new FileInfo(path).Length : null;
    }

    public async Task DeleteAsync(string objectKey, CancellationToken ct)
    {
        if (_s3 != null)
        {
            try
            {
                await _s3.DeleteObjectAsync(Bucket, objectKey, ct);
            }
            catch (AmazonS3Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete LoRA object s3://{Bucket}/{Key}", Bucket, objectKey);
                throw;
            }
            return;
        }

        var path = LocalPath(objectKey);
        if (File.Exists(path)) File.Delete(path);
    }

    private string LocalPath(string objectKey)
    {
        var root = Path.GetFullPath(
            string.IsNullOrWhiteSpace(_opts.LoraAssets.LocalDir)
                ? Path.Combine(_opts.LocalArtifactDir, "loras")
                : _opts.LoraAssets.LocalDir);
        var relative = objectKey.Replace('\\', '/').TrimStart('/');
        if (relative.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase))
            relative = relative[(Prefix.Length + 1)..];
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid LoRA object key");
        return full;
    }

    private static string SanitizePathSegment(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return "_";
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Replace('/', '_').Replace('\\', '_');
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return "";
    }

    public void Dispose() => _s3?.Dispose();
}
