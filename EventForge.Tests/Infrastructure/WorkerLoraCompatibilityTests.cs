using EventForge.Core;
using EventForge.Infrastructure;
using EventForge.Queue;
using FluentAssertions;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class WorkerLoraCompatibilityTests
{
    private const string WanJobWithLoras = """
        {
          "type": "assign_job",
          "model": "wan2",
          "graph": {
            "10": {
              "class_type": "LoraLoaderModelOnly",
              "inputs": { "lora_name": "creampie_wan22_e50_high.safetensors" }
            },
            "11": {
              "class_type": "LoraLoaderModelOnly",
              "inputs": { "lora_name": "creampie_wan22_e50_low.safetensors" }
            }
          }
        }
        """;

    [Fact]
    public void ExtractRequiredLoras_reads_lora_loader_nodes()
    {
        var loras = WorkerLoraCompatibility.ExtractRequiredLoras(WanJobWithLoras);
        loras.Should().BeEquivalentTo([
            "creampie_wan22_e50_high.safetensors",
            "creampie_wan22_e50_low.safetensors",
        ]);
    }

    [Fact]
    public void HasAllRequiredLoras_false_when_known_loras_missing_one()
    {
        var assets = WorkerModelAssets.FromJson("""{"loras":["creampie_wan22_e50_high.safetensors"]}""");
        var required = WorkerLoraCompatibility.ExtractRequiredLoras(WanJobWithLoras);
        WorkerLoraCompatibility.HasAllRequiredLoras([], assets, required).Should().BeFalse();
    }

    [Fact]
    public void HasAllRequiredLoras_true_when_known_loras_has_both()
    {
        var known = new[] { "creampie_wan22_e50_high.safetensors", "creampie_wan22_e50_low.safetensors" };
        var assets = WorkerModelAssets.FromJson("{}");
        var required = WorkerLoraCompatibility.ExtractRequiredLoras(WanJobWithLoras);
        WorkerLoraCompatibility.HasAllRequiredLoras(known, assets, required).Should().BeTrue();
    }

    [Fact]
    public void TryClaimAny_skips_job_when_worker_lacks_required_lora()
    {
        var queue = new InMemoryJobQueue();
        queue.Enqueue(new JobRecord
        {
            JobId = "wan-lora",
            AppId = "app",
            Capability = "wan",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = WanJobWithLoras,
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        queue.Enqueue(new JobRecord
        {
            JobId = "wan-plain",
            AppId = "app",
            Capability = "wan",
            Tier = "bulk",
            Kind = JobKind.Image,
            PayloadJson = """{"type":"assign_job","model":"wan2","graph":{}}""",
            Status = JobStatus.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        });

        var assets = WorkerModelAssets.FromJson("""
            {"loras":["other.safetensors"],"unets":["Wan2.2/wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors","Wan2.2/wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors"]}
            """);
        var knownLoras = new[] { "other.safetensors" };

        bool CanClaim(JobRecord job)
        {
            var model = JobPayloadReader.ExtractModelKey(job.PayloadJson);
            if (!WorkerModelCompatibility.CanRunModel(assets, model, "loboforge-wan-native-1", job.Capability))
                return false;
            var required = WorkerLoraCompatibility.ExtractRequiredLoras(job.PayloadJson);
            return WorkerLoraCompatibility.HasAllRequiredLoras(knownLoras, assets, required);
        }

        var claimed = queue.TryClaimAny(["wan"], "wrath", "loboforge-wan-native-1", TimeSpan.FromMinutes(5), CanClaim);

        claimed.Should().NotBeNull();
        claimed!.JobId.Should().Be("wan-plain");
    }
}
