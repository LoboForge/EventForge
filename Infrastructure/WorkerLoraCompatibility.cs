using System.Text.Json;

namespace EventForge.Infrastructure;

/// <summary>
/// Mirror of agent/loboforge_agent_common.py LoRA claim checks — gate claims by known_loras + Comfy inventory.
/// </summary>
public static class WorkerLoraCompatibility
{
    private static readonly string[] LoraLoaderTypes =
    [
        "LoraLoader",
        "LoraLoaderModelOnly",
        "Power Lora Loader (rgthree)",
    ];

    public static string NormalizeBasename(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var pathPart = raw.Split(':')[0].Trim().Replace('\\', '/');
        var name = Path.GetFileName(pathPart);
        return string.IsNullOrWhiteSpace(name) ? "" : name;
    }

    public static bool BasenamesMatch(string a, string b) =>
        string.Equals(NormalizeBasename(a), NormalizeBasename(b), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ExtractRequiredLoras(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return [];

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return ExtractFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<string> ExtractFromElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return [];

        if (el.TryGetProperty("type", out var typeEl)
            && typeEl.ValueKind == JsonValueKind.String
            && string.Equals(typeEl.GetString(), "assign_job", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractFromGraph(el);
        }

        if (el.TryGetProperty("payload", out var inner))
            return ExtractFromElement(inner);

        return ExtractFromGraph(el);
    }

    private static List<string> ExtractFromGraph(JsonElement assign)
    {
        if (!assign.TryGetProperty("graph", out var graph) || graph.ValueKind != JsonValueKind.Object)
            return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var required = new List<string>();

        foreach (var node in graph.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object) continue;
            var nodeEl = node.Value;
            if (!nodeEl.TryGetProperty("class_type", out var ctEl) || ctEl.ValueKind != JsonValueKind.String)
                continue;
            var classType = ctEl.GetString() ?? "";
            if (!LoraLoaderTypes.Contains(classType, StringComparer.Ordinal)) continue;
            if (!nodeEl.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object)
                continue;

            foreach (var name in CollectLoraNamesFromInputs(inputs))
            {
                var key = NormalizeBasename(name).ToLowerInvariant();
                if (key.Length == 0 || !seen.Add(key)) continue;
                required.Add(name);
            }
        }

        return required;
    }

    private static IEnumerable<string> CollectLoraNamesFromInputs(JsonElement inputs)
    {
        if (inputs.TryGetProperty("lora_name", out var loraNameEl) && loraNameEl.ValueKind == JsonValueKind.String)
        {
            var n = loraNameEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
        }

        if (inputs.TryGetProperty("lora", out var loraEl) && loraEl.ValueKind == JsonValueKind.String)
        {
            var n = loraEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
        }

        // Power Lora Loader (rgthree) stores slots as lora_1, lora_2, … objects.
        foreach (var prop in inputs.EnumerateObject())
        {
            if (!prop.Name.StartsWith("lora_", StringComparison.OrdinalIgnoreCase)) continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (prop.Value.TryGetProperty("lora", out var slotLora) && slotLora.ValueKind == JsonValueKind.String)
            {
                var n = slotLora.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(n)) yield return n;
            }
        }
    }

    public static bool HasAllRequiredLoras(
        IReadOnlyList<string> knownLoras,
        WorkerModelAssets assets,
        IReadOnlyList<string> required)
    {
        if (required.Count == 0) return true;
        foreach (var req in required)
        {
            if (!WorkerHasLora(knownLoras, assets, req))
                return false;
        }
        return true;
    }

    public static bool WorkerHasLora(
        IReadOnlyList<string> knownLoras,
        WorkerModelAssets assets,
        string loraName)
    {
        if (string.IsNullOrWhiteSpace(loraName)) return true;
        var target = NormalizeBasename(loraName);
        if (target.Length == 0) return true;

        foreach (var k in knownLoras)
        {
            if (BasenamesMatch(k, target)) return true;
        }

        foreach (var k in assets.Assets)
        {
            if (BasenamesMatch(k, target)) return true;
        }

        return false;
    }
}
