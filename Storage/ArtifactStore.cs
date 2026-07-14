using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using EventForge.Configuration;
using Microsoft.Extensions.Options;

namespace EventForge.Storage;

public interface IArtifactStore
{
    bool Enabled { get; }
    Task<(string Url, string ContentType)> SaveStreamAsync(
        string jobId, string fileName, string contentType, Stream body, CancellationToken ct);
    Task SaveInputStreamAsync(
        string jobId, string fileName, string contentType, Stream body, CancellationToken ct);
    Task<(Stream Stream, string ContentType, long Length)?> TryOpenInputStreamAsync(
        string jobId, string fileName, CancellationToken ct);
    Task DeleteJobArtifactsAsync(string jobId, CancellationToken ct);
    Task DeleteJobArtifactsBatchAsync(IReadOnlyList<string> jobIds, CancellationToken ct);
}

public sealed class ArtifactStore : IArtifactStore, IDisposable
{
    private readonly EventForgeOptions _opts;
    private readonly IAmazonS3? _s3;
    private readonly SemaphoreSlim _uploadGate;
    private readonly ILogger<ArtifactStore> _log;

    public ArtifactStore(IOptions<EventForgeOptions> options, ILogger<ArtifactStore> log)
    {
        _opts = options.Value;
        _log = log;
        _uploadGate = new SemaphoreSlim(Math.Max(1, _opts.MaxConcurrentUploads));

        if (_opts.Artifacts.Enabled && !string.IsNullOrWhiteSpace(_opts.Artifacts.Bucket))
        {
            var region = string.IsNullOrWhiteSpace(_opts.Artifacts.Region) ? _opts.AwsRegion : _opts.Artifacts.Region;
            _s3 = new AmazonS3Client(RegionEndpoint.GetBySystemName(region.Trim()));
        }
    }

    public bool Enabled => _s3 != null || Directory.Exists(_opts.LocalArtifactDir) || true;

    public Task SaveInputStreamAsync(
        string jobId, string fileName, string contentType, Stream body, CancellationToken ct) =>
        SaveBlobAsync(jobId, fileName, contentType, body, inputs: true, ct);

    public async Task<(Stream Stream, string ContentType, long Length)?> TryOpenInputStreamAsync(
        string jobId, string fileName, CancellationToken ct)
    {
        var safeName = string.IsNullOrWhiteSpace(fileName) ? "input.bin" : Path.GetFileName(fileName);
        if (_s3 != null)
        {
            var key = InputObjectKey(jobId, safeName);
            try
            {
                var resp = await _s3.GetObjectAsync(_opts.Artifacts.Bucket.Trim(), key, ct);
                return (resp.ResponseStream, resp.Headers.ContentType ?? GuessContentType(safeName), resp.ContentLength);
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        var path = InputLocalPath(jobId, safeName);
        if (!File.Exists(path)) return null;
        var fs = File.OpenRead(path);
        return (fs, GuessContentType(safeName), new FileInfo(path).Length);
    }

    public async Task<(string Url, string ContentType)> SaveStreamAsync(
        string jobId, string fileName, string contentType, Stream body, CancellationToken ct)
    {
        var (_, ctOut, url) = await SaveBlobAsync(jobId, fileName, contentType, body, inputs: false, ct);
        return (url, ctOut);
    }

    private async Task<(string SafeName, string ContentType, string Url)> SaveBlobAsync(
        string jobId, string fileName, string contentType, Stream body, bool inputs, CancellationToken ct)
    {
        if (!await _uploadGate.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new UploadSaturationException(15);
        try
        {
            var safeName = string.IsNullOrWhiteSpace(fileName)
                ? (inputs ? "input.bin" : "output.bin")
                : Path.GetFileName(fileName);
            var ctOut = string.IsNullOrWhiteSpace(contentType) ? GuessContentType(safeName) : contentType;

            if (_s3 != null)
            {
                var key = inputs
                    ? InputObjectKey(jobId, safeName)
                    : $"{_opts.Artifacts.Prefix.TrimEnd('/')}/{jobId}/{safeName}";
                var bytes = await ReadAllBytesAsync(body, ct);
                await using var uploadStream = new MemoryStream(bytes, writable: false);
                var request = new PutObjectRequest
                {
                    BucketName = _opts.Artifacts.Bucket.Trim(),
                    Key = key,
                    InputStream = uploadStream,
                    ContentType = ctOut,
                };
                request.Headers.ContentLength = bytes.LongLength;
                await _s3.PutObjectAsync(request, ct);
                return (safeName, ctOut, $"s3://{_opts.Artifacts.Bucket.Trim()}/{key}");
            }

            var path = inputs
                ? InputLocalPath(jobId, safeName)
                : Path.Combine(Path.GetFullPath(_opts.LocalArtifactDir), jobId, safeName);
            if (!inputs)
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var fs = File.Create(path);
            await body.CopyToAsync(fs, ct);
            return (safeName, ctOut, $"file://{path}");
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    private string InputObjectKey(string jobId, string safeName) =>
        $"{_opts.Artifacts.Prefix.TrimEnd('/')}/{jobId}/inputs/{safeName}";

    private string InputLocalPath(string jobId, string safeName)
    {
        var dir = Path.Combine(Path.GetFullPath(_opts.LocalArtifactDir), jobId, "inputs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, safeName);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream body, CancellationToken ct)
    {
        if (body is MemoryStream mem && mem.TryGetBuffer(out var segment))
        {
            if (mem.Position != 0 || mem.Length != segment.Count)
            {
                mem.Position = 0;
                return mem.ToArray();
            }
            return segment.AsSpan().ToArray();
        }

        using var ms = new MemoryStream();
        await body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string GuessContentType(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".json" => "application/json",
            ".txt" => "text/plain",
            _ => "application/octet-stream",
        };
    }

    public async Task DeleteJobArtifactsBatchAsync(IReadOnlyList<string> jobIds, CancellationToken ct)
    {
        // Parallelize list+delete so large ops purges finish within HTTP timeouts.
        var errors = new System.Collections.Concurrent.ConcurrentBag<(string JobId, Exception Ex)>();
        await Parallel.ForEachAsync(
            jobIds,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (jobId, token) =>
            {
                try
                {
                    await DeleteJobArtifactsAsync(jobId, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add((jobId, ex));
                }
            });

        if (!errors.IsEmpty)
        {
            var snapshot = errors.ToArray();
            var first = snapshot[0];
            throw new AggregateException(
                $"Failed to delete S3 artifacts for {snapshot.Length}/{jobIds.Count} job(s); first={first.JobId}: {first.Ex.Message}",
                snapshot.Select(e => e.Ex));
        }
    }

    public async Task DeleteJobArtifactsAsync(string jobId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jobId)) return;
        var id = jobId.Trim();
        if (_s3 != null)
        {
            var prefix = $"{_opts.Artifacts.Prefix.TrimEnd('/')}/{id}/";
            var bucket = _opts.Artifacts.Bucket.Trim();
            try
            {
                string? token = null;
                do
                {
                    var list = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucket,
                        Prefix = prefix,
                        ContinuationToken = token,
                    }, ct);
                    var keys = list.S3Objects.Select(o => new KeyVersion { Key = o.Key }).ToList();
                    if (keys.Count > 0)
                    {
                        var del = await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                        {
                            BucketName = bucket,
                            Objects = keys,
                            Quiet = false,
                        }, ct);
                        if (del.DeleteErrors is { Count: > 0 } errs)
                        {
                            var sample = errs[0];
                            throw new AmazonS3Exception(
                                $"DeleteObjects failed for s3://{bucket}/{sample.Key}: {sample.Code} {sample.Message}");
                        }
                    }
                    token = list.IsTruncated ? list.NextContinuationToken : null;
                } while (token != null);
            }
            catch (AmazonS3Exception ex)
            {
                _log.LogError(
                    ex,
                    "S3 artifact delete failed for job {JobId} bucket={Bucket} prefix={Prefix} status={Status} error={ErrorCode}",
                    id, bucket, prefix, ex.StatusCode, ex.ErrorCode);
                throw;
            }
            return;
        }

        var localDir = Path.Combine(Path.GetFullPath(_opts.LocalArtifactDir), id);
        if (Directory.Exists(localDir))
            Directory.Delete(localDir, recursive: true);
    }

    public void Dispose() => _s3?.Dispose();
}
