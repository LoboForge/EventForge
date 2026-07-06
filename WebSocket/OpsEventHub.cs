using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EventForge.Models;
using ClientWebSocket = System.Net.WebSockets.WebSocket;

namespace EventForge.WebSocket;

/// <summary>Live ops dashboard WebSocket fan-out (separate from app consumer /v1/ws).</summary>
public sealed class OpsEventHub
{
    private readonly ConcurrentDictionary<Guid, OpsSession> _sessions = new();

    public void Add(OpsSession session) => _sessions.TryAdd(session.Id, session);

    public void Remove(OpsSession session) => _sessions.TryRemove(session.Id, out _);

    public async Task PublishAsync(string eventType, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload, WsJson.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var session in _sessions.Values.ToArray())
            await session.SendAsync(bytes, eventType, ct);
    }

    public async Task PublishFleetSnapshotAsync(object snapshot, CancellationToken ct = default) =>
        await PublishAsync("ops.fleet.snapshot", snapshot, ct);
}

public sealed class OpsSession
{
    private readonly ClientWebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly HashSet<string> _subscriptions = new(StringComparer.Ordinal);

    public OpsSession(ClientWebSocket socket)
    {
        _socket = socket;
        Id = Guid.NewGuid();
        _subscriptions.Add("ops.fleet.snapshot");
        _subscriptions.Add("ops.worker.check_in");
        _subscriptions.Add("ops.job.started");
        _subscriptions.Add("ops.job.completed");
        _subscriptions.Add("ops.job.failed");
        _subscriptions.Add("ops.job.timeout");
        _subscriptions.Add("ops.job.released");
    }

    public Guid Id { get; }

    public void SetSubscriptions(IEnumerable<string> events)
    {
        _subscriptions.Clear();
        foreach (var e in events)
            if (!string.IsNullOrWhiteSpace(e))
                _subscriptions.Add(e.Trim());
    }

    public bool IsSubscribed(string eventType) => _subscriptions.Contains(eventType);

    public async Task SendJsonAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, WsJson.Options);
        await SendAsync(Encoding.UTF8.GetBytes(json), null, ct);
    }

    public async Task SendAsync(byte[] bytes, string? eventType, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(eventType) && !IsSubscribed(eventType)) return;
        if (_socket.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            if (_socket.State != WebSocketState.Open) return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
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
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
