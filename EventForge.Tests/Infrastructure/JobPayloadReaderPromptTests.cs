using EventForge.Infrastructure;
using Xunit;

namespace EventForge.Tests.Infrastructure;

public sealed class JobPayloadReaderPromptTests
{
    [Fact]
    public void ExtractPrompt_reads_top_level_and_nested_prompt_fields()
    {
        var json = """
            {
              "type": "assign_job",
              "model": "flux",
              "prompt": "a red fox",
              "negative_prompt": "blurry",
              "graph": {
                "1": {
                  "class_type": "CLIPTextEncode",
                  "inputs": { "text": "extra clip text" }
                }
              }
            }
            """;
        var prompt = JobPayloadReader.ExtractPrompt(json);
        Assert.NotNull(prompt);
        Assert.Contains("a red fox", prompt);
        Assert.Contains("blurry", prompt);
        Assert.Contains("extra clip text", prompt);
    }

    [Fact]
    public void ExtractSearchablePromptText_matches_literal_case_insensitive_substring()
    {
        var json = """{"prompt":"Hello BANANA World"}""";
        var text = JobPayloadReader.ExtractSearchablePromptText(json);
        Assert.Contains("banana", text);
        Assert.DoesNotContain("BANANA", text); // lowercased
        Assert.True(text.Contains("banana", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExtractPrompt_returns_null_when_no_prompt_fields()
    {
        Assert.Null(JobPayloadReader.ExtractPrompt("""{"type":"assign_job","model":"flux"}"""));
        Assert.Equal("", JobPayloadReader.ExtractSearchablePromptText("{}"));
    }
}
