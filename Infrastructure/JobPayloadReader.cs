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
}
