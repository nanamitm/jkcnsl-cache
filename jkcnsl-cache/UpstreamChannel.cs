using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace jkcnsl_cache;

public sealed class UpstreamChannel : UpstreamChannelBase
{
    private readonly string _upstreamUrl;
    private readonly int _fallbackMaxCommentLength;
    private readonly object _controlMessageLock = new();
    private readonly List<byte[]> _controlMessages = new();
    private string? _upstreamThreadId;
    private long _upstreamVposBaseTicks; // 0 = 未取得、正値 = UTC Ticks

    public override string? CurrentTarget => IsRunning ? _upstreamUrl : null;
    public override string Status => IsLocalFallbackActive ? "fallbackLocal" : base.Status;
    public override string? StatusText => IsLocalFallbackActive ? "ローカル待避中" : null;
    public override DateTimeOffset? VposBaseTime
    {
        get
        {
            if (IsLocalFallbackActive)
                return base.VposBaseTime;
            var t = Interlocked.Read(ref _upstreamVposBaseTicks);
            return t > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;
        }
    }

    // 投稿キュー
    private readonly Channel<PostRequest> _postChannel =
        Channel.CreateBounded<PostRequest>(new BoundedChannelOptions(8)
            { FullMode = BoundedChannelFullMode.Wait });
    private TaskCompletionSource<ReadOnlyMemory<byte>>? _pendingPostTcs;

    private sealed record PostRequest(
        byte[] Json,
        TaskCompletionSource<ReadOnlyMemory<byte>> Tcs,
        CancellationToken TimeoutToken)
    {
        public bool IsCanceled => TimeoutToken.IsCancellationRequested;
    }

    public override async Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        if (IsLocalFallbackActive)
            return await PostLocalFallbackCommentAsync(json, null, _fallbackMaxCommentLength, ct);

        var tcs = new TaskCompletionSource<ReadOnlyMemory<byte>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        await _postChannel.Writer.WriteAsync(new PostRequest(json.ToArray(), tcs, timeoutCts.Token),
            timeoutCts.Token);
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "{\"type\":\"error\",\"data\":{\"code\":\"TIMEOUT\"}}"u8.ToArray();
        }
    }

    public UpstreamChannel(string channel, string upstreamUrl, IConfiguration config, ILogger logger, MetricsService metrics)
        : base(channel, logger, metrics, config)
    {
        _upstreamUrl = upstreamUrl;
        _fallbackMaxCommentLength = Math.Max(1, config.GetValue<int>("CacheServer:LocalStream:MaxCommentLength", 75));
    }

    public override async Task AddClientAndWaitAsync(WebSocket ws, CancellationToken ct)
    {
        foreach (var message in GetControlMessagesSnapshot())
            await ws.SendAsync(message, WebSocketMessageType.Text, true, ct);

        await base.AddClientAndWaitAsync(ws, ct);
    }

    protected override async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        try
        {
        using var watchWs = new ClientWebSocket();
        using var commentWs = new ClientWebSocket();
        commentWs.Options.AddSubProtocol("msg.nicovideo.jp#json");
        watchWs.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");
        commentWs.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");

        _logger.LogInformation("[{Channel}] 上流(避難所)へ接続: {Url}", _channel, _upstreamUrl);
        await watchWs.ConnectAsync(new Uri(_upstreamUrl), ct);
        await SendTextAsync(watchWs,
            """{"type":"startWatching","data":{"room":{"protocol":"webSocket","commentable":true},"reconnect":false}}""", ct);

        var state = new ConnectionState();
        var watchBuf = new byte[32768];
        int watchCount = 0;
        var commentBuf = new byte[32768];
        int commentCount = 0;

        Task<WebSocketReceiveResult>? watchRecvTask = null;
        Task<WebSocketReceiveResult>? commentRecvTask = null;
        Task keepSeatTask = Task.Delay(state.KeepSeatIntervalMs, ct);
        Task? postWaitTask = null;

        while (!ct.IsCancellationRequested && watchWs.State == WebSocketState.Open)
        {
            watchRecvTask ??= watchWs.ReceiveAsync(
                new ArraySegment<byte>(watchBuf, watchCount, watchBuf.Length - watchCount), ct);
            if (state.CommentConnected)
                commentRecvTask ??= commentWs.ReceiveAsync(
                    new ArraySegment<byte>(commentBuf, commentCount, commentBuf.Length - commentCount), ct);
            // 投稿待ちがなければ次のキューエントリを待つ
            if (postWaitTask == null && _pendingPostTcs == null)
                postWaitTask = _postChannel.Reader.WaitToReadAsync(ct).AsTask();

            var tasks = new List<Task> { watchRecvTask, keepSeatTask };
            if (commentRecvTask != null) tasks.Add(commentRecvTask);
            if (postWaitTask != null) tasks.Add(postWaitTask);
            var completed = await Task.WhenAny(tasks);

            if (completed == postWaitTask)
            {
                postWaitTask = null;
                if (_postChannel.Reader.TryRead(out var req))
                {
                    if (req.IsCanceled)
                        continue;
                    _pendingPostTcs = req.Tcs;
                    await watchWs.SendAsync(req.Json, WebSocketMessageType.Text, true, ct);
                }
                continue;
            }

            if (completed == keepSeatTask)
            {
                if (watchWs.State == WebSocketState.Open)
                    await SendTextAsync(watchWs, """{"type":"keepSeat"}""", ct);
                keepSeatTask = Task.Delay(state.KeepSeatIntervalMs, ct);
                continue;
            }

            if (completed == watchRecvTask)
            {
                var r = await watchRecvTask;
                watchRecvTask = null;
                if (r.MessageType == WebSocketMessageType.Close) break;
                watchCount += r.Count;
                if (r.EndOfMessage)
                {
                    await HandleWatchMessageAsync(watchWs, commentWs, watchBuf, watchCount, state, ct);
                    watchCount = 0;
                }
                continue;
            }

            if (completed == commentRecvTask)
            {
                var r = await commentRecvTask;
                commentRecvTask = null;
                if (r.MessageType == WebSocketMessageType.Close) break;
                commentCount += r.Count;
                if (r.EndOfMessage)
                {
                    var msg = NormalizeDownstreamMessage(commentBuf.AsMemory(0, commentCount));
                    if (ShouldCacheControlMessage(msg))
                        CacheControlMessage(msg);
                    TryRecordChatComment(msg);
                    await BroadcastAsync(msg);
                    commentCount = 0;
                }
            }
        }

        // 接続断時: 待機中の投稿にエラーを返す
        _pendingPostTcs?.TrySetResult(
            "{\"type\":\"error\",\"data\":{\"code\":\"CONNECTION_LOST\"}}"u8.ToArray());
        _pendingPostTcs = null;
        SetLocalFallbackActive(true);
        }
        catch
        {
            SetLocalFallbackActive(true);
            throw;
        }
    }

    private async Task HandleWatchMessageAsync(ClientWebSocket watchWs, ClientWebSocket commentWs,
        byte[] buf, int count, ConnectionState state, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(buf.AsMemory(0, count));
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;

        switch (typeProp.GetString())
        {
            case "postCommentResult":
            case "error":
                _pendingPostTcs?.TrySetResult(buf.AsSpan(0, count).ToArray());
                _pendingPostTcs = null;
                break;

            case "ping":
                await SendTextAsync(watchWs, """{"type":"pong"}""", ct);
                break;

            case "seat":
                if (doc.RootElement.TryGetProperty("data", out var seatData) &&
                    seatData.TryGetProperty("keepIntervalSec", out var intervalProp))
                {
                    state.KeepSeatIntervalMs = Math.Clamp(intervalProp.GetInt32(), 10, 300) * 1000;
                }
                break;

            case "room":
                if (state.CommentConnected) break;
                ClearControlMessages();
                if (!doc.RootElement.TryGetProperty("data", out var roomData)) break;
                var threadId = roomData.TryGetProperty("threadId", out var tid) ? tid.GetString() : _channel;
                var postKey = roomData.TryGetProperty("yourPostKey", out var pk) ? pk.GetString() ?? "" : "";
                if (!roomData.TryGetProperty("messageServer", out var ms)) break;
                if (!ms.TryGetProperty("uri", out var uriProp)) break;
                var commentUri = uriProp.GetString();
                if (commentUri?.StartsWith("wss://") != true) break;

                _upstreamThreadId = threadId;
                if (roomData.TryGetProperty("vposBaseTime", out var vbtProp) &&
                    vbtProp.GetString() is { } vbtStr &&
                    DateTimeOffset.TryParse(vbtStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var vbt))
                    Interlocked.Exchange(ref _upstreamVposBaseTicks, vbt.UtcTicks);
                await commentWs.ConnectAsync(new Uri(commentUri), ct);
                SetLocalFallbackActive(false);
                var openMsg = $$$"""[{"ping":{"content":"rs: 0"}},{"ping":{"content":"ps: 0"}},{"thread":{"thread":"{{{threadId}}}","threadkey":"{{{postKey}}}","user_id":"guest","nicoru":0,"res_from":-10,"scores":1,"version":"20061206","with_global":1}},{"ping":{"content":"pf: 0"}},{"ping":{"content":"rf: 0"}}]""";
                await SendTextAsync(commentWs, openMsg, ct);
                state.CommentConnected = true;
                _logger.LogInformation("[{Channel}] コメントセッション接続完了", _channel);
                ResetReconnectBackoff();
                break;
        }
    }

    private ReadOnlyMemory<byte> NormalizeDownstreamMessage(ReadOnlyMemory<byte> message)
    {
        if (_upstreamThreadId == null) return message;
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
                return NormalizeDownstreamArray(root, message);
            if (root.ValueKind != JsonValueKind.Object)
                return message;

            if (!TryGetThreadObject(root, out var propertyName, out var payload))
                return message;
            if (!TryMapThreadId(payload, out var newThread))
                return message;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            WriteNormalizedThreadObject(writer, propertyName, payload, newThread);
            writer.WriteEndObject();
            writer.Flush();
            return ms.ToArray().AsMemory();
        }
        catch { return message; }
    }

    private ReadOnlyMemory<byte> NormalizeDownstreamArray(JsonElement root, ReadOnlyMemory<byte> originalMessage)
    {
        var changed = false;
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartArray();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                TryGetThreadObject(item, out var propertyName, out var payload) &&
                TryMapThreadId(payload, out var newThread))
            {
                writer.WriteStartObject();
                WriteNormalizedThreadObject(writer, propertyName, payload, newThread);
                writer.WriteEndObject();
                changed = true;
            }
            else
            {
                item.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
        writer.Flush();
        return changed ? ms.ToArray().AsMemory() : originalMessage;
    }

    private static bool TryGetThreadObject(JsonElement root, out string propertyName, out JsonElement payload)
    {
        if (root.TryGetProperty("chat", out payload))
        {
            propertyName = "chat";
            return true;
        }
        if (root.TryGetProperty("thread", out payload))
        {
            propertyName = "thread";
            return true;
        }

        propertyName = "";
        payload = default;
        return false;
    }

    private bool TryMapThreadId(JsonElement payload, out string newThread)
    {
        newThread = "";
        if (!payload.TryGetProperty("thread", out var threadProp))
            return false;
        var thread = threadProp.GetString();
        if (thread == null || _upstreamThreadId == null)
            return false;

        var downstreamThreadId = GetDownstreamThreadId();
        if (thread == _upstreamThreadId)
        {
            newThread = downstreamThreadId;
            return true;
        }
        if (thread.StartsWith(_upstreamThreadId + "_", StringComparison.Ordinal))
        {
            newThread = downstreamThreadId + "_" + thread[(_upstreamThreadId.Length + 1)..];
            return true;
        }

        return false;
    }

    private static void WriteNormalizedThreadObject(Utf8JsonWriter writer,
        string propertyName, JsonElement payload, string newThread)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartObject();
        foreach (var prop in payload.EnumerateObject())
        {
            if (prop.Name == "thread")
                writer.WriteString("thread", newThread);
            else
                prop.WriteTo(writer);
        }
        writer.WriteEndObject();
    }

    private static bool ShouldCacheControlMessage(ReadOnlyMemory<byte> message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
                return root.TryGetProperty("thread", out _) || IsPastChatPing(root);
            if (root.ValueKind == JsonValueKind.Array)
                return root.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.Object &&
                    (item.TryGetProperty("thread", out _) || IsPastChatPing(item)));
        }
        catch { }

        return false;
    }

    private static bool IsPastChatPing(JsonElement root)
    {
        if (!root.TryGetProperty("ping", out var ping) ||
            !ping.TryGetProperty("content", out var contentElement))
            return false;
        var content = contentElement.GetString();
        return content != null &&
            (content.StartsWith("ps:", StringComparison.Ordinal) ||
             content.StartsWith("pf:", StringComparison.Ordinal));
    }

    private byte[][] GetControlMessagesSnapshot()
    {
        lock (_controlMessageLock)
            return _controlMessages.ToArray();
    }

    private void CacheControlMessage(ReadOnlyMemory<byte> message)
    {
        lock (_controlMessageLock)
        {
            _controlMessages.Add(message.ToArray());
            if (_controlMessages.Count > 16)
                _controlMessages.RemoveAt(0);
        }
    }

    private void ClearControlMessages()
    {
        lock (_controlMessageLock)
            _controlMessages.Clear();
    }

    private string GetDownstreamThreadId() =>
        _channel.EndsWith("r", StringComparison.Ordinal) &&
        _channel.Length > 2 &&
        _channel[0] == 'j' &&
        _channel[1] == 'k' &&
        _channel.AsSpan(2, _channel.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0
            ? _channel[..^1]
            : _channel;

    private void TryRecordChatComment(ReadOnlyMemory<byte> message)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            if (doc.RootElement.TryGetProperty("chat", out var chat) &&
                chat.TryGetProperty("no", out var no))
                RecordComment(no.GetInt64());
        }
        catch { }
    }

    private sealed class ConnectionState
    {
        public bool CommentConnected { get; set; }
        public int KeepSeatIntervalMs { get; set; } = 60_000;
    }
}
