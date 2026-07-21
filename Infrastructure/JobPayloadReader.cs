using System.Text.Json;

namespace EventForge.Infrastructure;

/// <summary>Extract assign_job model keys from enqueued job payloads.</summary>
public static class JobPayloadReader
{
    public static string? ExtractModelKey(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return ExtractFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractFromElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (el.TryGetProperty("type", out var typeEl)
            && typeEl.ValueKind == JsonValueKind.String
            && string.Equals(typeEl.GetString(), "assign_job", StringComparison.OrdinalIgnoreCase))
        {
            return ReadModel(el);
        }

        if (el.TryGetProperty("caption", out var caption) && caption.ValueKind == JsonValueKind.True)
            return "joycaption";

        if (el.TryGetProperty("payload", out var inner))
            return ExtractFromElement(inner);

        return ReadModel(el);
    }

    private static string? ReadModel(JsonElement el)
    {
        if (!el.TryGetProperty("model", out var modelEl) || modelEl.ValueKind != JsonValueKind.String)
            return null;
        var model = modelEl.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(model) ? null : model;
    }

    public static string? ExtractExternalId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return ExtractExternalIdFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractExternalIdFromElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (el.TryGetProperty("external_id", out var extEl) && extEl.ValueKind == JsonValueKind.String)
        {
            var ext = extEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(ext)) return ext;
        }

        if (el.TryGetProperty("payload", out var inner))
            return ExtractExternalIdFromElement(inner);

        return null;
    }

    // Field names that carry the human-facing generation prompt. Kept small and explicit so
    // ops moderation only surfaces/matches prompt text, never binary/image content.
    private static readonly string[] PromptFieldNames =
    {
        "prompt", "positive_prompt", "positive", "negative_prompt", "negative",
        "text", "caption", "description",
    };

    /// <summary>
    /// Best-effort single prompt string for display in the ops console. Prefers a positive/main
    /// prompt; falls back to any prompt-like text (including ComfyUI CLIPTextEncode nodes).
    /// Returns null when no prompt-like text exists.
    /// </summary>
    public static string? ExtractPrompt(string? payloadJson)
    {
        var all = CollectPromptTexts(payloadJson);
        if (all.Count == 0) return null;
        var joined = string.Join(" | ", all.Distinct(StringComparer.Ordinal));
        return string.IsNullOrWhiteSpace(joined) ? null : Trim(joined, 2000);
    }

    /// <summary>
    /// Lowercased concatenation of every prompt-like text in the payload, used for
    /// case-insensitive literal keyword moderation matching. Empty string when none.
    /// </summary>
    public static string ExtractSearchablePromptText(string? payloadJson)
    {
        var all = CollectPromptTexts(payloadJson);
        if (all.Count == 0) return "";
        return string.Join("\n", all).ToLowerInvariant();
    }

    private static List<string> CollectPromptTexts(string? payloadJson)
    {
        var results = new List<string>();
        if (string.IsNullOrWhiteSpace(payloadJson)) return results;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            CollectPromptTexts(doc.RootElement, results, depth: 0);
        }
        catch (JsonException)
        {
            // ignore — non-JSON payloads have no structured prompt
        }
        return results;
    }

    private static void CollectPromptTexts(JsonElement el, List<string> results, int depth)
    {
        if (depth > 8 || results.Count > 64) return;

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && IsPromptField(prop.Name))
                    {
                        var text = prop.Value.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            results.Add(text);
                    }
                    else if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        CollectPromptTexts(prop.Value, results, depth + 1);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        CollectPromptTexts(item, results, depth + 1);
                }
                break;
        }
    }

    private static bool IsPromptField(string name)
    {
        foreach (var candidate in PromptFieldNames)
        {
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
