using EventForge.Configuration;
using EventForge.Models;
using EventForge.Persistence;
using EventForge.Storage;
using Microsoft.Extensions.Options;

namespace EventForge.Services;

public sealed class LoraAssetService
{
    private readonly EventForgeOptions _opts;
    private readonly LoraAssetCatalog _catalog;
    private readonly ILoraAssetBlobStore _blobs;
    private readonly WriteBehindPersistence _persist;
    private readonly ILogger<LoraAssetService> _log;

    public LoraAssetService(
        IOptions<EventForgeOptions> options,
        LoraAssetCatalog catalog,
        ILoraAssetBlobStore blobs,
        WriteBehindPersistence persist,
        ILogger<LoraAssetService> log)
    {
        _opts = options.Value;
        _catalog = catalog;
        _blobs = blobs;
        _persist = persist;
        _log = log;
    }

    public Task InitializeAsync(CancellationToken ct) => _catalog.InitializeAsync(ct);

    public long MaxUploadBytes => _opts.LoraAssets.MaxBytes > 0
        ? _opts.LoraAssets.MaxBytes
        : 5L * 1024 * 1024 * 1024;

    public async Task<(LoraAssetRecord Asset, string UploadMethod, string UploadUrl, IReadOnlyDictionary<string, string> UploadHeaders)?>
        BeginUploadAsync(
            string appId,
            string fileName,
            string? modes,
            long? expectedBytes,
            string? sha256,
            bool replace,
            CancellationToken ct)
    {
        var safeName = NormalizeFileName(fileName);
        if (safeName == null)
            throw new ArgumentException("file_name must be a .safetensors basename");

        var maxBytes = MaxUploadBytes;
        if (expectedBytes is > 0 && expectedBytes > maxBytes)
            throw new ArgumentException($"file exceeds MaxBytes ({maxBytes})");

        var existing = _catalog.ListForApp(appId)
            .FirstOrDefault(r => string.Equals(r.FileName, safeName, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (!replace && string.Equals(existing.Status, LoraAssetStatus.Ready, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("asset_exists");
            await DeleteInternalAsync(existing, ct);
        }

        var assetId = Guid.NewGuid().ToString("N");
        var contentType = "application/octet-stream";
        var objectKey = _blobs.ObjectKey(appId, assetId, safeName);
        var record = new LoraAssetRecord
        {
            AssetId = assetId,
            AppId = appId.Trim(),
            FileName = safeName,
            ObjectKey = objectKey,
            ContentType = contentType,
            Bytes = expectedBytes is > 0 ? expectedBytes : null,
            Sha256 = string.IsNullOrWhiteSpace(sha256) ? null : sha256.Trim().ToLowerInvariant(),
            Modes = NormalizeModes(modes),
            Status = LoraAssetStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _catalog.UpsertAsync(record, ct);

        var ttl = TimeSpan.FromMinutes(Math.Clamp(_opts.LoraAssets.PresignTtlMinutes, 5, 24 * 60));
        var presigned = await _blobs.CreatePresignedPutUrlAsync(objectKey, contentType, ttl, ct);
        if (!string.IsNullOrWhiteSpace(presigned))
        {
            return (record, "PUT", presigned!, new Dictionary<string, string>
            {
                ["Content-Type"] = contentType,
            });
        }

        var publicBase = (_opts.PublicUrl ?? "").Trim().TrimEnd('/');
        if (publicBase.Length == 0) publicBase = "http://localhost:8090";
        var proxyUrl = $"{publicBase}/v1/assets/loras/{assetId}/content";
        return (record, "PUT", proxyUrl, new Dictionary<string, string>
        {
            ["Content-Type"] = contentType,
            ["Authorization"] = "Bearer <api-key>",
        });
    }

    public async Task<LoraAssetRecord?> SaveContentAsync(
        string appId, string assetId, Stream body, string? contentType, CancellationToken ct)
    {
        var record = _catalog.TryGet(assetId);
        if (record == null || !string.Equals(record.AppId, appId, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(record.Status, LoraAssetStatus.Pending, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(record.Status, LoraAssetStatus.Failed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("asset_not_pending");

        var maxBytes = MaxUploadBytes;
        await using var limited = new MaxLengthReadStream(body, maxBytes);
        await _blobs.PutAsync(record.ObjectKey, contentType ?? record.ContentType, limited, ct);
        record.Bytes = limited.BytesRead;
        record.Status = LoraAssetStatus.Pending;
        await _catalog.UpsertAsync(record, ct);
        return record;
    }

    public async Task<LoraAssetRecord?> CompleteAsync(
        string appId, string assetId, long? bytes, string? sha256, CancellationToken ct)
    {
        var record = _catalog.TryGet(assetId);
        if (record == null || !string.Equals(record.AppId, appId, StringComparison.OrdinalIgnoreCase))
            return null;

        var size = await _blobs.TryGetSizeAsync(record.ObjectKey, ct);
        var minReady = _opts.LoraAssets.MinReadyBytes > 0 ? _opts.LoraAssets.MinReadyBytes : 1_000_000;
        if (size is null || size < minReady)
        {
            record.Status = LoraAssetStatus.Failed;
            await _catalog.UpsertAsync(record, ct);
            throw new InvalidOperationException("object_missing_or_too_small");
        }

        var maxBytes = MaxUploadBytes;
        if (size > maxBytes)
        {
            record.Status = LoraAssetStatus.Failed;
            await _catalog.UpsertAsync(record, ct);
            throw new InvalidOperationException("file_too_large");
        }

        if (bytes is > 0 && bytes != size)
            throw new InvalidOperationException("bytes_mismatch");

        record.Bytes = size;
        if (!string.IsNullOrWhiteSpace(sha256))
            record.Sha256 = sha256.Trim().ToLowerInvariant();
        record.Status = LoraAssetStatus.Ready;
        record.CompletedAt = DateTimeOffset.UtcNow;
        await _catalog.UpsertAsync(record, ct);
        _log.LogInformation(
            "LoRA asset ready app={App} file={File} bytes={Bytes} id={Id}",
            record.AppId, record.FileName, record.Bytes, record.AssetId);
        return record;
    }

    public IReadOnlyList<LoraAssetRecord> List(string appId, string? status = null) =>
        _catalog.ListForApp(appId, status);

    public LoraAssetRecord? GetForApp(string appId, string assetId)
    {
        var record = _catalog.TryGet(assetId);
        if (record == null || !string.Equals(record.AppId, appId, StringComparison.OrdinalIgnoreCase))
            return null;
        return record;
    }

    public async Task<bool> DeleteForAppAsync(string appId, string assetId, CancellationToken ct)
    {
        var record = GetForApp(appId, assetId);
        if (record == null) return false;
        await DeleteInternalAsync(record, ct);
        return true;
    }

    public async Task<(Stream Stream, string ContentType, long Length, string FileName)?> TryOpenForJobAsync(
        string jobId, string fileName, string workerId, CancellationToken ct)
    {
        var job = await _persist.TryGetJobAsync(jobId.Trim(), ct);
        if (job == null) return null;

        // Any authenticated worker may pull LoRAs for a job's app (needed after release/prefetch).
        _ = workerId;
        var safeName = Path.GetFileName((fileName ?? "").Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(safeName)) return null;

        var asset = _catalog.TryGetReadyByAppFile(job.AppId, safeName);
        if (asset == null) return null;

        var opened = await _blobs.TryOpenAsync(asset.ObjectKey, ct);
        if (opened == null) return null;
        return (opened.Value.Stream, opened.Value.ContentType, opened.Value.Length, asset.FileName);
    }

    public bool AppHasReadyLoras(string appId, IEnumerable<string> fileNames) =>
        _catalog.HasAllReadyForApp(appId, fileNames);

    public bool AppHasReadyLora(string appId, string fileName) =>
        _catalog.IsReadyForApp(appId, fileName);

    private async Task DeleteInternalAsync(LoraAssetRecord record, CancellationToken ct)
    {
        try
        {
            await _blobs.DeleteAsync(record.ObjectKey, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "LoRA blob delete failed for {AssetId} key={Key}", record.AssetId, record.ObjectKey);
        }
        await _catalog.DeleteAsync(record.AssetId, ct);
    }

    public static string? NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var name = Path.GetFileName(fileName.Trim().Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (!name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)) return null;
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\'))
            return null;
        return name;
    }

    public static string NormalizeModes(string? modes)
    {
        var raw = (modes ?? "all").Trim().ToLowerInvariant();
        if (raw.Length == 0) return "all";
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p switch
            {
                "wan" or "wan-native" => "video",
                "ltx" or "ltx-native" => "all",
                _ => p,
            })
            .Where(p => p is "image" or "video" or "all")
            .Distinct()
            .ToList();
        return parts.Count == 0 ? "all" : string.Join(",", parts);
    }

    /// <summary>Readable wrapper that rejects bodies larger than <paramref name="max"/>.</summary>
    private sealed class MaxLengthReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _max;
        public long BytesRead { get; private set; }

        public MaxLengthReadStream(Stream inner, long max)
        {
            _inner = inner;
            _max = max;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Accumulate(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken);
            Accumulate(n);
            return n;
        }

        private void Accumulate(int n)
        {
            if (n <= 0) return;
            BytesRead += n;
            if (BytesRead > _max)
                throw new InvalidOperationException("file_too_large");
        }

        protected override void Dispose(bool disposing)
        {
            // Do not dispose the request body stream.
            base.Dispose(disposing);
        }
    }
}
