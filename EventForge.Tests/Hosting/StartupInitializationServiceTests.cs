using EventForge.Configuration;
using EventForge.Infrastructure;
using EventForge.Persistence;
using EventForge.Queue;
using EventForge.Services;
using EventForge.Storage;
using EventForge.Tests.Support;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventForge.Tests.Hosting;

public sealed class StartupInitializationServiceTests
{
    [Fact]
    public async Task StartAsync_returns_immediately_while_restore_and_load_continue_in_background()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"ef-startup-{Guid.NewGuid():N}.db");
        try
        {
            var opts = Options.Create(new EventForgeOptions { SqlitePath = tempDb });
            var queue = new InMemoryJobQueue();
            var events = new SqliteEventStore(opts);
            var persist = new WriteBehindPersistence(opts, queue, events, NullLogger<WriteBehindPersistence>.Instance);
            var delayed = new DelayedSqliteS3Persistence(TimeSpan.FromMilliseconds(800), tempDb);
            var blobs = new LoraAssetBlobStore(opts, NullLogger<LoraAssetBlobStore>.Instance);
            var catalog = new LoraAssetCatalog(opts, NullLogger<LoraAssetCatalog>.Instance);
            var loras = new LoraAssetService(opts, catalog, blobs, persist, NullLogger<LoraAssetService>.Instance);
            var accounts = new AccountStore(opts);
            var svc = new StartupInitializationService(
                delayed,
                persist,
                events,
                loras,
                accounts,
                NullLogger<StartupInitializationService>.Instance);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await svc.StartAsync(CancellationToken.None);
            sw.Stop();

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
            persist.IsLoaded.Should().BeFalse();

            await WaitUntilAsync(() => persist.IsLoaded, TimeSpan.FromSeconds(5));
            persist.IsLoaded.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(tempDb)) File.Delete(tempDb);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
