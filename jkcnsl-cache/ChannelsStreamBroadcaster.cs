using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace jkcnsl_cache;

public sealed class ChannelsStreamBroadcaster
{
    private readonly ConcurrentDictionary<Guid, StreamClient> _clients = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Guid Add(WebSocket ws)
    {
        var id = Guid.NewGuid();
        _clients[id] = new StreamClient(ws);
        return id;
    }

    public void Remove(Guid id)
    {
        if (_clients.TryRemove(id, out var client))
            client.Dispose();
    }

    public Task SendAsync(Guid id, object payload, CancellationToken ct) =>
        _clients.TryGetValue(id, out var client)
            ? client.SendAsync(payload, ct)
            : Task.CompletedTask;

    public async Task BroadcastAsync(object payload, CancellationToken ct)
    {
        foreach (var (id, client) in _clients.ToArray())
        {
            if (client.WebSocket.State != WebSocketState.Open)
            {
                Remove(id);
                continue;
            }

            try { await client.SendAsync(payload, ct); }
            catch
            {
                Remove(id);
            }
        }
    }

    public async Task CloseAllAsync(CancellationToken ct)
    {
        foreach (var (id, client) in _clients.ToArray())
        {
            try
            {
                if (client.WebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await client.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server stopping", ct);
            }
            catch { }
            finally
            {
                Remove(id);
            }
        }
    }

    private sealed class StreamClient(WebSocket webSocket) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocket WebSocket { get; } = webSocket;

        public async Task SendAsync(object payload, CancellationToken ct)
        {
            if (WebSocket.State != WebSocketState.Open) return;

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct);
            try
            {
                if (WebSocket.State == WebSocketState.Open)
                    await WebSocket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}
