using System.Text.Json;
using EventForge.Models;
using EventForge.Storage;

namespace EventForge.WebSocket;

public sealed class WsProtocolHandler
{
    private static readonly HashSet<string> AllowedClientTypes = new(StringComparer.Ordinal)
    {
        "hello", "subscribe", "replay", "ping", "pong",
    };

    private static readonly HashSet<string> ForbiddenClientTypes = new(StringComparer.Ordinal)
    {
        ForgeEventTypes.Completed, ForgeEventTypes.Failed, "dispatch",
    };

    private readonly IEventStore _store;

    public WsProtocolHandler(IEventStore store) => _store = store;

    public async Task RunSessionAsync(WsSession session, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested)
        {
            var raw = await session.ReceiveTextAsync(buffer, ct);
            if (raw == null) break;
            session.TouchActivity();

            ClientMessage? msg;
            try { msg = JsonSerializer.Deserialize<ClientMessage>(raw, WsJson.Options); }
            catch
            {
                await session.SendJsonAsync(new { type = "error", code = "invalid_json", message = "Could not parse JSON" }, ct);
                continue;
            }

            if (msg?.Type == null)
            {
                await session.SendJsonAsync(new { type = "error", code = "missing_type", message = "Message type required" }, ct);
                continue;
            }

            if (ForbiddenClientTypes.Contains(msg.Type))
            {
                await session.SendJsonAsync(new
                {
                    type = "error",
                    code = "publish_forbidden",
                    message = "Clients may only subscribe and replay; publishing completions is not allowed",
                }, ct);
                continue;
            }

            if (!AllowedClientTypes.Contains(msg.Type))
            {
                await session.SendJsonAsync(new { type = "error", code = "unknown_type", message = $"Unknown type: {msg.Type}" }, ct);
                continue;
            }

            switch (msg.Type)
            {
                case "hello":
                    await session.SendJsonAsync(new { type = "hello", ok = true, app_id = session.AppId, protocol = 1 }, ct);
                    session.HelloAcknowledged = true;
                    break;
                case "subscribe":
                    session.SetSubscriptions(msg.Events ?? Array.Empty<string>());
                    await session.SendJsonAsync(new { type = "subscribed", events = msg.Events ?? Array.Empty<string>() }, ct);
                    break;
                case "replay":
                    await HandleReplayAsync(session, msg.Since, ct);
                    break;
                case "ping":
                    await session.SendJsonAsync(new { type = "pong" }, ct);
                    break;
                case "pong":
                    break;
            }
        }
    }

    private async Task HandleReplayAsync(WsSession session, string? sinceRaw, CancellationToken ct)
    {
        if (!DateTimeOffset.TryParse(sinceRaw, out var since))
        {
            await session.SendJsonAsync(new { type = "error", code = "invalid_since", message = "replay.since must be ISO8601" }, ct);
            return;
        }

        var records = await _store.QuerySinceAsync(session.AppId, since, ct);
        var events = records
            .Select(ManifestParser.ToServerMessage)
            .Where(e => session.IsSubscribed(e.Type))
            .ToList();

        await session.SendJsonAsync(new { type = "replay.batch", count = events.Count, events }, ct);
    }
}
