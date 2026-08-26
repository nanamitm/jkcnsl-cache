using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace jkcnsl_cache;

public sealed class ChannelsStreamBroadcaster
{
    private readonly ConcurrentDictionary<Guid, StreamClient> _clients = new();
    private readonly TimeSpan _sendTimeout;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public ChannelsStreamBroadcaster(IConfiguration config)
    {
        _sendTimeout = TimeSpan.FromSeconds(Math.Max(1,
            config.GetValue<int>("CacheServer:BroadcastSendTimeoutSeconds", 5)));
    }

    public Guid Add(WebSocket ws)
    {
        var id = Guid.NewGuid();
        _clients[id] = new StreamClient(ws);
        return id;
    }

    public void Remove(Guid id) => _clients.TryRemove(id, out _);

    public Task SendAsync(Guid id, object payload, CancellationToken ct)
    {
        if (!_clients.TryGetValue(id, out var client))
            return Task.CompletedTask;
        var bytes = Serialize(payload);
        return SendToClientAsync(id, client, bytes, ct);
    }

    // 全クライアントへ並列送信する。応答が無いクライアントが1件でもいると
    // 逐次送信では他クライアントへの配信やこのメソッドの呼び出し元（ProgramInfoService の
    // 定期ループ）まで無期限に止まってしまうため、クライアントごとにタイムアウトを掛けて
    // 詰まった接続だけを切断する。JSONシリアライズは全クライアント共通なので1回だけ行い、
    // 得られたバイト列をそのまま使い回す（クライアントごとに再シリアライズしない）。
    public Task BroadcastAsync(object payload, CancellationToken ct)
    {
        var bytes = Serialize(payload);
        var tasks = _clients.ToArray()
            .Select(kv => SendToClientAsync(kv.Key, kv.Value, bytes, ct));
        return Task.WhenAll(tasks);
    }

    private static ReadOnlyMemory<byte> Serialize(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    private async Task SendToClientAsync(Guid id, StreamClient client, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (client.WebSocket.State != WebSocketState.Open)
        {
            Remove(id);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_sendTimeout);
        try
        {
            await client.SendAsync(bytes, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼び出し元のキャンセル（切断・アプリ終了）。タイムアウト扱いにせず、
            // 通常のクローズ処理（CloseAllAsync や接続ハンドラの finally）に任せる。
        }
        catch
        {
            Remove(id);
            try
            {
                if (client.WebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await client.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                        "broadcast send timeout", CancellationToken.None);
            }
            catch { }
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

    private sealed class StreamClient(WebSocket webSocket)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public WebSocket WebSocket { get; } = webSocket;

        public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            if (WebSocket.State != WebSocketState.Open) return;

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
    }
}
