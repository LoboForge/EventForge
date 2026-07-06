using System.Text.Json;
using EventForge.Api;
using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Api;

public sealed class WorkerCheckInTests
{
    [Fact]
    public void ParseCheckIn_reads_eventforge_payload()
    {
        using var doc = JsonDocument.Parse("""
            {
              "node_uuid": "abc",
              "hostname": "wrath-image-99",
              "gpu_name": "A100",
              "vram_total": 81920,
              "vram_free": 70000,
              "disk_free_mb": 90000,
              "transport": "eventforge",
              "busy": true,
              "current_job_uuid": "job-1",
              "queue_access_ok": true,
              "forge_queue_capabilities": ["flux-klein", "wan"],
              "known_loras": ["style.safetensors"]
            }
            """);

        var payload = WorkerEndpoints.ParseCheckIn(doc.RootElement);
        payload.NodeUuid.Should().Be("abc");
        payload.Hostname.Should().Be("wrath-image-99");
        payload.Transport.Should().Be("eventforge");
        payload.Busy.Should().BeTrue();
        payload.CurrentJobUuid.Should().Be("job-1");
        payload.ForgeQueueCapabilities.Should().Contain("wan");
        payload.KnownLoras.Should().Contain("style.safetensors");
    }
}
