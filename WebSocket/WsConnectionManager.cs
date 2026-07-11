using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EventForge.Models;
using EventForge.Storage;
using ClientWebSocket = System.Net.WebSockets.WebSocket;

namespace EventForge.WebSocket;

public sealed class WsConnectionManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, WsSession>> _byApp = new();

    public void Add(WsSession session)
    {
        var appSessions = _byApp.GetOrAdd(session.AppId, _ => new());
        appSessions.TryAdd(session.Id, session);
    }

    public void Remove(WsSession session)
    {
        if (_byApp.TryGetValue(session.AppId, out var clients))
        {
            clients.TryRemove(session.Id, out _);
            if (clients.IsEmpty)
                _byApp.TryRemove(session.AppId, out _);
        }
    }

    public async Task BroadcastAsync(string appId, ServerEventMessage message, CancellationToken ct) =>
        await BroadcastAsync(appId, (object)message, message.Type, ct);

    public async Task BroadcastAsync(string appId, object message, CancellationToken ct)
    {
        var type = message.GetType().GetProperty("type")?.GetValue(message)?.ToString()
            ?? message.GetType().GetProperty("Type")?.GetValue(message)?.ToString()
            ?? "";
        await BroadcastAsync(appId, message, type, ct);
    }

    public async Task BroadcastAsync(string appId, object message, string eventType, CancellationToken ct)
    {
        if (!_byApp.TryGetValue(appId, out var clients)) return;
        var json = JsonSerializer.Serialize(message, WsJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var session in clients.Values)
        {
            if (!string.IsNullOrWhiteSpace(eventType) && !session.IsSubscribed(eventType)) continue;
            await session.SendAsync(bytes, ct);
        }
    }
}

public sealed class WsSession
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);

    public WsSession(ClientWebSocket socket, string appId)
    {
        _socket = socket;
        AppId = appId;
        Id = Guid.NewGuid();
    }

    public Guid Id { get; }
    public string AppId { get; }
    public bool HelloAcknowledged { get; set; }
    public DateTimeOffset LastActivityUtc { get; private set; } = DateTimeOffset.UtcNow;

    public void TouchActivity() => LastActivityUtc = DateTimeOffset.UtcNow;

    public void SetSubscriptions(IEnumerable<string> events)
    {
        _subscriptions.Clear();
        foreach (var e in events)
            _subscriptions.Add(e);
    }

    public bool IsSubscribed(string eventType) => _subscriptions.Contains(eventType);

    public async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, WsJson.Options);
        await SendAsync(Encoding.UTF8.GetBytes(json), ct);
    }

    public async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            TouchActivity();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<string?> ReceiveTextAsync(byte[] buffer, CancellationToken ct)
    {
        if (_socket.State != WebSocketState.Open) return null;
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        TouchActivity();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async Task CloseAsync(string reason)
    {
        try
        {
            if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { /* best effort */ }
        try { _socket.Abort(); } catch { }
    }
}
