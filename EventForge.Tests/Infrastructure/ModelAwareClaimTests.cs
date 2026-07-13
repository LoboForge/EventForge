using EventForge.Core;
using EventForge.Infrastructure;
using EventForge.Queue;
using EventForge.Services;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class WorkerModelCompatibilityTests
{
    [Fact]
    public void CanRunModel_blocks_music_without_ace_step()
    {
        var assets = WorkerModelAssets.FromJson("""
            {"checkpoints":["ltx-2.3-22b-distilled.safetensors"],"unets":["ltx-2.3-22b.safetensors"]}
            """);
        WorkerModelCompatibility.CanRunModel(assets, "music", "loboforge-ltx-43579394", "ltx")
            .Should().BeFalse();
        WorkerModelCompatibility.CanRunModel(assets, "ltx23", "loboforge-ltx-43579394", "ltx")
            .Should().BeTrue();
    }

    [Fact]
    public void IsNeverFailCapability_matches_ollama_caps()
    {
        WorkerModelCompatibility.IsNeverFailCapability("ollama-chat").Should().BeTrue();
        WorkerModelCompatibility.IsNeverFailCapability("dolphin").Should().BeTrue();
        WorkerModelCompatibility.IsNeverFailCapability("wan").Should().BeFalse();
    }

    [Fact]
    public void CanRunModel_allows_dolphin3_on_ollama_chat_without_comfy_inventory()
    {
        var assets = WorkerModelAssets.FromJson("{}");
        WorkerModelCompatibility.CanRunModel(assets, "dolphin3:8b", "09f50e615b42", "ollama-chat")
            .Should().BeTrue();
        WorkerModelCompatibility.CanRunModel(assets, "dolphin3:8b", "loboforge-ollama-42600549", "ollama-chat")
            .Should().BeTrue();
    }

    [Fact]
    public void CanRunModel_allows_music_when_ace_step_present()
    {
        var assets = WorkerModelAssets.FromJson("""
            {"checkpoints":["ace_step_v1_3.5b.safetensors","ltx-2.3-22b-distilled.safetensors"]}
            """);
        WorkerModelCompatibility.CanRunModel(assets, "music", "loboforge-video-44190901", "wan")
            .Should().BeTrue();
    }
    [Fact]
    public void CanRunModel_allows_joycaption_on_joycaption_hostname_without_assets()
    {
        var assets = WorkerModelAssets.FromJson("{}");
        WorkerModelCompatibility.CanRunModel(assets, "joycaption", "joycaption-44059162", "caption")
            .Should().BeTrue();
    }
}

public sealed class ModelAwareClaimTests
{
    private static JobRecord MusicJob() => new()
    {
        JobId = "music-1",
        AppId = "app",
        Capability = "wan",
        Tier = "normal",
        Kind = JobKind.Image,
        PayloadJson = """{"type":"assign_job","model":"music","prompt":"test"}""",
        Status = JobStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static JobRecord LtxJob() => new()
    {
        JobId = "ltx-1",
        AppId = "app",
        Capability = "ltx",
        Tier = "normal",
        Kind = JobKind.Image,
        PayloadJson = """{"type":"assign_job","model":"ltx23","prompt":"test"}""",
        Status = JobStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void TryClaimAny_skips_music_when_worker_lacks_ace_step()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MusicJob());
        var assets = WorkerModelAssets.FromJson("""
            {"checkpoints":["ltx-2.3-22b-distilled.safetensors"],"unets":["ltx-2.3-22b.safetensors"]}
            """);
        bool CanClaim(JobRecord job)
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return WorkerModelCompatibility.CanRunModel(assets, model, "loboforge-ltx-43579394", job.Capability);
        }

        var claimed = queue.TryClaimAny(["wan"], "wrath", "loboforge-video-44190901", TimeSpan.FromMinutes(5), CanClaim);

        claimed.Should().BeNull();
    }

    [Fact]
    public void TryClaimAny_claims_music_on_wan_capability()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MusicJob());
        var assets = WorkerModelAssets.FromJson("""
            {"checkpoints":["ace_step_v1_3.5b.safetensors"]}
            """);
        bool CanClaim(JobRecord job)
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return WorkerModelCompatibility.CanRunModel(assets, model, "loboforge-video-44190901", job.Capability);
        }

        var claimed = queue.TryClaimAny(["wan"], "wrath", "loboforge-video-44190901", TimeSpan.FromMinutes(5), CanClaim);

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("music-1");
    }

    [Fact]
    public void RemapLegacyMusicCapabilities_moves_queued_music_off_ltx()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(new JobRecord
        {
            JobId = "legacy-music",
            AppId = "app",
            Capability = "ltx",
            Tier = "normal",
            Kind = JobKind.Image,
            PayloadJson = """{"type":"assign_job","model":"music","prompt":"test"}""",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        queue.RemapLegacyMusicCapabilities().Should().Be(1);
        queue.Get("legacy-music")!.Capability.Should().Be("wan");
    }

    [Fact]
    public void TryClaimAny_claims_caption_when_joycaption_hostname_has_no_models_json()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(new JobRecord
        {
            JobId = "cap-1",
            AppId = "app",
            Capability = "caption",
            Tier = "normal",
            Kind = JobKind.Image,
            PayloadJson = """{"type":"assign_job","model":"joycaption","caption":true}""",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var assets = WorkerModelAssets.FromJson(null);
        bool CanClaim(JobRecord job)
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return WorkerModelCompatibility.CanRunModel(assets, model, "joycaption-44059162", job.Capability);
        }

        var claimed = queue.TryClaimAny(["caption"], "wrath", "joycaption-44059162", TimeSpan.FromMinutes(5), CanClaim);

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("cap-1");
    }

    [Fact]
    public void TryClaimAny_claims_ltx_job_when_models_match()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(MusicJob());
        queue.Enqueue(LtxJob());
        var assets = WorkerModelAssets.FromJson("""
            {"checkpoints":["ltx-2.3-22b-distilled.safetensors"],"unets":["ltx-2.3-22b-distilled-1.1.safetensors"]}
            """);
        bool CanClaim(JobRecord job)
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            return WorkerModelCompatibility.CanRunModel(assets, model, "loboforge-ltx-43579394", job.Capability);
        }

        var claimed = queue.TryClaimAny(["ltx"], "wrath", "loboforge-ltx-43579394", TimeSpan.FromMinutes(5), CanClaim);

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("ltx-1");
    }
}
