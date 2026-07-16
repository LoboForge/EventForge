namespace EventForge.Storage;

public interface ILoraAssetBlobStore
{
    bool S3Enabled { get; }

    string ObjectKey(string appId, string assetId, string fileName);

    Task<string?> CreatePresignedPutUrlAsync(
        string objectKey, string contentType, TimeSpan ttl, CancellationToken ct);

    Task PutAsync(string objectKey, string contentType, Stream body, CancellationToken ct);

    Task<(Stream Stream, string ContentType, long Length)?> TryOpenAsync(string objectKey, CancellationToken ct);

    Task<long?> TryGetSizeAsync(string objectKey, CancellationToken ct);

    Task DeleteAsync(string objectKey, CancellationToken ct);
}
