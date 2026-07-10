namespace EventForge.Storage;

/// <summary>Guards against replacing a large S3 SQLite snapshot with an empty post-restart DB.</summary>
public static class SqliteBackupPolicy
{
    public const long MinTrustedBytes = 96 * 1024;
    public const int MinTrustedJobs = 10;

    public static bool ShouldRefuseOverwrite(int localJobs, long localBytes, int remoteJobs, long remoteBytes)
    {
        if (remoteBytes <= 0 && remoteJobs <= 0) return false;

        if (remoteJobs >= MinTrustedJobs && localJobs == 0)
            return true;

        if (remoteJobs >= 50 && localJobs < remoteJobs / 2)
            return true;

        if (remoteBytes >= 500_000 && localBytes > 0 && localBytes < remoteBytes / 10)
            return true;

        if (remoteBytes >= MinTrustedBytes && localBytes > 0 && localBytes < MinTrustedBytes
            && remoteJobs >= MinTrustedJobs && localJobs < MinTrustedJobs)
            return true;

        return false;
    }

    public static bool IsTrustedSnapshot(int jobCount, long bytes) =>
        jobCount >= MinTrustedJobs || bytes >= MinTrustedBytes;
}
