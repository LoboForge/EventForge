namespace EventForge.Storage;

/// <summary>Raised when artifact upload slots are full — clients should retry.</summary>
public sealed class UploadSaturationException : Exception
{
    public UploadSaturationException(int retryAfterSeconds)
        : base($"Upload slots saturated — retry after {retryAfterSeconds}s")
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public int RetryAfterSeconds { get; }
}
