using System.Diagnostics;
using System.Net;
using System.Text.Json;
using EventForge.Configuration;
using EventForge.Storage;
using EventForge.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EventForge.Tests.Hosting;

public sealed class HealthStartupIntegrationTests
{
    [Fact]
    public async Task Health_responds_before_slow_startup_restore_finishes()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"ef-it-{Guid.NewGuid():N}.db");
        try
        {
            await using var app = CreateTestApp(tempDb, TimeSpan.FromSeconds(15));
            await app.StartAsync();
            using var client = CreateClient(app);

            var sw = Stopwatch.StartNew();
            using var response = await client.GetAsync("/health");
            sw.Stop();

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("cache_loaded").GetBoolean().Should().BeFalse();
        }
        finally
        {
            TryDelete(tempDb);
        }
    }

    [Fact]
    public async Task Health_reports_cache_loaded_after_background_initialization()
    {
        var tempDb = Path.Combine(Path.GetTempPath(), $"ef-it-{Guid.NewGuid():N}.db");
        try
        {
            await using var app = CreateTestApp(tempDb, TimeSpan.FromMilliseconds(200));
            await app.StartAsync();
            using var client = CreateClient(app);

            await WaitForCacheLoadedAsync(client, TimeSpan.FromSeconds(10));

            using var response = await client.GetAsync("/health");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("cache_loaded").GetBoolean().Should().BeTrue();
        }
        finally
        {
            TryDelete(tempDb);
        }
    }

    private static WebApplication CreateTestApp(string tempDb, TimeSpan restoreDelay)
    {
        return EventForgeApplication.Create(Array.Empty<string>(), builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{EventForgeOptions.Section}:ListenUrl"] = "http://127.0.0.1:0",
                [$"{EventForgeOptions.Section}:SqlitePath"] = tempDb,
                [$"{EventForgeOptions.Section}:S3:Enabled"] = "false",
            });
        }, services =>
        {
            ReplaceSingleton(services, typeof(ISqliteS3Persistence),
                new DelayedSqliteS3Persistence(restoreDelay, tempDb));
        });
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        var address = app.Urls.FirstOrDefault()
            ?? throw new InvalidOperationException("EventForge test host did not bind a URL");
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private static void ReplaceSingleton(IServiceCollection services, Type serviceType, object instance)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == serviceType);
        if (existing != null)
            services.Remove(existing);
        services.AddSingleton(serviceType, instance);
    }

    private static async Task WaitForCacheLoadedAsync(HttpClient client, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/health");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (doc.RootElement.GetProperty("cache_loaded").GetBoolean())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Timed out waiting for cache_loaded=true");
    }

    private static void TryDelete(string path)
    {
        if (!File.Exists(path)) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
