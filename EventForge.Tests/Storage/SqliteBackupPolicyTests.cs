using EventForge.Storage;
using Xunit;

namespace EventForge.Tests.Storage;

public sealed class SqliteBackupPolicyTests
{
    [Theory]
    [InlineData(0, 28_672, 400, 50_000_000, true)]
    [InlineData(200, 5_000_000, 400, 50_000_000, false)]
    [InlineData(10, 200_000, 400, 50_000_000, true)]
    [InlineData(5, 500_000, 0, 0, false)]
    public void ShouldRefuseOverwrite_matches_shrink_scenarios(
        int localJobs, long localBytes, int remoteJobs, long remoteBytes, bool expectRefuse)
    {
        Assert.Equal(expectRefuse,
            SqliteBackupPolicy.ShouldRefuseOverwrite(localJobs, localBytes, remoteJobs, remoteBytes));
    }

    [Fact]
    public void IsTrustedSnapshot_requires_jobs_or_bytes()
    {
        Assert.False(SqliteBackupPolicy.IsTrustedSnapshot(0, 28_672));
        Assert.True(SqliteBackupPolicy.IsTrustedSnapshot(12, 1024));
        Assert.True(SqliteBackupPolicy.IsTrustedSnapshot(0, 200_000));
    }
}
