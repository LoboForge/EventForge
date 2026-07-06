using System.Text.Json;
using System.Text.Json.Serialization;
using EventForge.Storage;

namespace EventForge.Models;

public static class ForgeEventTypes
{
    public const string Started = "forge.job.started";
    public const string Completed = "forge.job.completed";
    public const string Failed = "forge.job.failed";
    public const string Timeout = "forge.job.timeout";
    public const string Released = "forge.job.released";
    public const string StreamStart = "forge.stream.start";
    public const string StreamToken = "forge.stream.token";
    public const string StreamDone = "forge.stream.done";
    public const string StreamReplace = "forge.stream.replace";
}

public static class WsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class ClientMessage
{
    public string? Type { get; set; }
    public int? Protocol { get; set; }
    public string[]? Events { get; set; }
    public string? Since { get; set; }
}

public sealed class ServerEventMessage
{
    public string Type { get; set; } = "";
    public string EventId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string JobId { get; set; } = "";
    public JsonElement Manifest { get; set; }
    public string? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class ManifestParseResult
{
    public required string JobId { get; init; }
    public required string AppId { get; init; }
    public required string EventType { get; init; }
    public required string ManifestJson { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? Error { get; init; }
}

public static class ManifestParser
{
    public static bool TryParse(string body, out ManifestParseResult? result, out string? skipReason)
    {
        result = null;
        skipReason = null;
        JsonElement root;
        try { root = JsonDocument.Parse(body).RootElement; }
        catch
        {
            skipReason = "invalid_json";
            return false;
        }

        var jobId = ReadString(root, "job_id", "jobId", "job_uuid");
        if (string.IsNullOrWhiteSpace(jobId))
        {
            skipReason = "missing_job_id";
            return false;
        }

        var appId = ReadString(root, "tenant_id", "tenantId") ?? "";
        if (string.IsNullOrWhiteSpace(appId))
        {
            skipReason = "empty_tenant_id";
            return false;
        }

        var status = (ReadString(root, "status") ?? "").Trim().ToLowerInvariant();
        var eventType = status switch
        {
            "completed" => ForgeEventTypes.Completed,
            "failed" or "cancelled" => ForgeEventTypes.Failed,
            _ => "",
        };
        if (eventType.Length == 0)
        {
            skipReason = $"unsupported_status:{status}";
            return false;
        }

        var completedRaw = ReadString(root, "completed_at", "completedAt");
        var completedAt = DateTimeOffset.TryParse(completedRaw, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

        var error = ReadString(root, "error", "reason");
        result = new ManifestParseResult
        {
            JobId = jobId,
            AppId = appId,
            EventType = eventType,
            ManifestJson = body,
            CompletedAt = completedAt,
            Error = error,
        };
        return true;
    }

    public static ServerEventMessage ToServerMessage(EventRecord record)
    {
        using var doc = JsonDocument.Parse(record.ManifestJson);
        return new ServerEventMessage
        {
            Type = record.EventType,
            EventId = record.EventId,
            AppId = record.AppId,
            JobId = record.JobId,
            Manifest = doc.RootElement.Clone(),
            CompletedAt = record.CompletedAt.ToString("O"),
            Error = record.Error,
        };
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        return null;
    }
}
