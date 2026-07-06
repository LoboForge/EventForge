using System.Text.Json;

namespace EventForge.Infrastructure;

/// <summary>Flattened Comfy model inventory from worker check-in.</summary>
public sealed class WorkerModelAssets
{
    public IReadOnlyList<string> Assets { get; init; } = [];
    public IReadOnlyList<string> Unets { get; init; } = [];

    public static WorkerModelAssets FromJson(string? modelsJson)
    {
        if (string.IsNullOrWhiteSpace(modelsJson))
            return new WorkerModelAssets();

        try
        {
            using var doc = JsonDocument.Parse(modelsJson);
            return FromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return new WorkerModelAssets();
        }
    }

    private static WorkerModelAssets FromElement(JsonElement root)
    {
        var assets = new List<string>();
        var unets = new List<string>();
        if (root.ValueKind != JsonValueKind.Object)
            return new WorkerModelAssets();

        foreach (var key in new[] { "unets", "checkpoints", "loras", "clips", "text_encoders", "vae" })
        {
            if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var name = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;
                assets.Add(name);
                if (string.Equals(key, "unets", StringComparison.OrdinalIgnoreCase))
                    unets.Add(name);
            }
        }

        return new WorkerModelAssets
        {
            Assets = assets,
            Unets = unets,
        };
    }
}
