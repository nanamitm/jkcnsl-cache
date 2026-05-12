using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace jkcnsl_cache;

public sealed class LocalStreamChannel : UpstreamChannelBase
{
    private static readonly TimeSpan RejectReportInterval = TimeSpan.FromMinutes(1);

    private readonly int _historyLimit;
    private readonly TimeSpan _historyDuration;
    private readonly int _queueLimit;
    private readonly int _dropDisconnectStreak;
    private readonly int _maxCommentLength;
    private readonly int _maxClients;
    private readonly int _maxTotalClients;
    private readonly LocalStreamConnectionLimiter _limiter;
    private readonly MetricsService _metrics;
    private readonly TimeSpan _dropStreakReset;
    private readonly ConcurrentDictionary<Guid, LocalClient> _localClients = new();
    private readonly Queue<HistoryMessage> _history = new();
    private readonly object _historyLock = new();
    private long _nextNo;
    private long _clientLimitRejects;
    private long _totalLimitRejects;

    public override string? CurrentTarget => "localstream:";
    public override string Status => "running";
    public override string? StatusText => "ローカルコメントストリーム";
    public override int ClientCount => _localClients.Values.Count(c => c.WebSocket.State == WebSocketState.Open);
    public override string GetDownstreamThreadId(string requestedChannel) =>
        NormalizeThreadId(requestedChannel);

    public LocalStreamChannel(string channel, IConfiguration config, ILogger logger,
        LocalStreamConnectionLimiter limiter, MetricsService metrics)
        : base(channel, logger, metrics)
    {
        _limiter = limiter;
        _metrics = metrics;
        _historyLimit = Math.Max(0, config.GetValue<int>("CacheServer:LocalStream:HistoryLimit", 20));
        _historyDuration = TimeSpan.FromSeconds(Math.Max(0,
            config.GetValue<int>("CacheServer:LocalStream:HistorySeconds", 3)));
        _maxClients = config.GetValue<int>("CacheServer:LocalStream:MaxClients", 100);
        _maxTotalClients = config.GetValue<int>("CacheServer:LocalStream:MaxTotalClients", 500);
        _queueLimit = Math.Max(1, config.GetValue<int>("CacheServer:LocalStream:QueueLimit", 100));
        _dropDisconnectStreak = Math.Max(1, config.GetValue<int>("CacheServer:LocalStream:DropDisconnectStreak", 5));
        _maxCommentLength = Math.Max(1, config.GetValue<int>("CacheServer:LocalStream:MaxCommentLength", 75));
        _dropStreakReset = TimeSpan.FromSeconds(Math.Max(1,
            config.GetValue<int>("CacheServer:LocalStream:DropStreakResetSeconds", 10)));
    }

    public override Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "postComment" ||
                !doc.RootElement.TryGetProperty("data", out var data))
                return Task.FromResult<ReadOnlyMemory<byte>>(Error("INVALID_REQUEST"));

            var text = data.TryGetProperty("text", out var textElement)
                ? NormalizeComment(textElement.GetString() ?? "")
                : "";
            if (string.IsNullOrWhiteSpace(text))
                return Task.FromResult<ReadOnlyMemory<byte>>(Error("EMPTY_COMMENT"));
            if (text.Length > _maxCommentLength)
                return Task.FromResult<ReadOnlyMemory<byte>>(Error("COMMENT_TOO_LONG"));

            var no = Interlocked.Increment(ref _nextNo);
            var vpos = data.TryGetProperty("vpos", out var vposElement) && vposElement.TryGetInt32(out var parsedVpos)
                ? Math.Max(0, parsedVpos)
                : 0;
            var mail = BuildMail(data);
            var now = DateTimeOffset.UtcNow;
            var userId = data.TryGetProperty("localUserId", out var userIdElement)
                ? NormalizeUserId(userIdElement.GetString() ?? "")
                : "";
            if (string.IsNullOrEmpty(userId))
                userId = $"local-{Random.Shared.NextInt64(0x100000000L):x8}";
            var chatJson = CreateChatJson(no, vpos, now, userId, mail, text);
            AddHistory(chatJson);
            RecordComment(no);
            _ = BroadcastLocalAsync(chatJson, CancellationToken.None);
            return Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(
                "{\"type\":\"postCommentResult\",\"data\":{\"status\":\"ok\",\"no\":" + no + "}}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Channel}] localstream 投稿処理に失敗しました", _channel);
            return Task.FromResult<ReadOnlyMemory<byte>>(Error("INTERNAL_ERROR"));
        }
    }

    public override async Task AddClientAndWaitAsync(WebSocket ws, string requestedChannel, CancellationToken ct)
    {
        if (_maxClients > 0 && ClientCount >= _maxClients)
        {
            Interlocked.Increment(ref _clientLimitRejects);
            _metrics.RecordChannelLimitReject();
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "localstream client limit", CancellationToken.None);
            return;
        }

        if (!_limiter.TryAcquire(_maxTotalClients, out var lease))
        {
            Interlocked.Increment(ref _totalLimitRejects);
            _metrics.RecordTotalLimitReject();
            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "localstream total client limit", CancellationToken.None);
            return;
        }

        var client = new LocalClient(Guid.NewGuid(), ws, _queueLimit, _dropDisconnectStreak,
            _dropStreakReset, GetDownstreamThreadId(requestedChannel), _metrics);
        _localClients[client.Id] = client;
        foreach (var message in GetHistorySnapshot())
            client.Enqueue(message);

        var sendTask = SendLoopAsync(client, ct);
        var receiveTask = ReceiveUntilCloseAsync(ws, ct);

        try { await Task.WhenAny(sendTask, receiveTask); }
        finally
        {
            _localClients.TryRemove(client.Id, out _);
            lease?.Dispose();
            client.Complete();
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
            }
        }
    }

    protected override async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        _logger.LogInformation("[{Channel}] localstream を開始しました", _channel);
        using var timer = new PeriodicTimer(RejectReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                FlushRejectReports();
        }
        catch (OperationCanceledException) { }
        finally
        {
            FlushRejectReports();
        }
    }

    private void FlushRejectReports()
    {
        var clientLimitRejects = Interlocked.Exchange(ref _clientLimitRejects, 0);
        if (clientLimitRejects > 0)
        {
            _logger.LogWarning(
                "[{Channel}] localstream クライアント数上限により接続を拒否しました: count={Count} max={MaxClients} intervalSeconds={IntervalSeconds}",
                _channel, clientLimitRejects, _maxClients, (int)RejectReportInterval.TotalSeconds);
        }

        var totalLimitRejects = Interlocked.Exchange(ref _totalLimitRejects, 0);
        if (totalLimitRejects > 0)
        {
            _logger.LogWarning(
                "[{Channel}] localstream 総クライアント数上限により接続を拒否しました: count={Count} maxTotal={MaxTotalClients} intervalSeconds={IntervalSeconds}",
                _channel, totalLimitRejects, _maxTotalClients, (int)RejectReportInterval.TotalSeconds);
        }
    }

    private async Task BroadcastLocalAsync(byte[] message, CancellationToken ct)
    {
        foreach (var client in _localClients.Values)
        {
            if (client.WebSocket.State != WebSocketState.Open)
                continue;

            var shouldDisconnect = client.Enqueue(message);
            _metrics.RecordDelivered();
            if (shouldDisconnect)
            {
                _metrics.RecordQueueDisconnect();
                _logger.LogWarning("[{Channel}] localstream 送信キュー超過が連続したため切断します: client={ClientId}",
                    _channel, client.Id);
                try
                {
                    await client.WebSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                        "send queue overflow", CancellationToken.None);
                }
                catch { }
            }
        }
    }

    private async Task SendLoopAsync(LocalClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && client.WebSocket.State == WebSocketState.Open)
        {
            var message = await client.DequeueAsync(ct);
            if (message == null) break;
            try
            {
                await client.WebSocket.SendAsync(NormalizeThread(message.Value, client.ThreadId),
                    WebSocketMessageType.Text, true, ct);
                client.MarkSent();
            }
            catch { break; }
        }
    }

    private static async Task ReceiveUntilCloseAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch { }
    }

    private void AddHistory(byte[] message)
    {
        if (_historyLimit == 0 || _historyDuration == TimeSpan.Zero) return;
        lock (_historyLock)
        {
            var now = DateTimeOffset.UtcNow;
            _history.Enqueue(new HistoryMessage(now, message));
            TrimHistory(now);
        }
    }

    private void TrimHistory(DateTimeOffset now)
    {
        while (_history.Count > 0 &&
               (now - _history.Peek().CreatedAt > _historyDuration || _history.Count > _historyLimit))
        {
            _history.Dequeue();
        }
    }

    private byte[][] GetHistorySnapshot()
    {
        if (_historyLimit == 0 || _historyDuration == TimeSpan.Zero)
            return [];

        lock (_historyLock)
        {
            TrimHistory(DateTimeOffset.UtcNow);
            return _history.Select(item => item.Message).ToArray();
        }
    }

    private sealed record HistoryMessage(DateTimeOffset CreatedAt, byte[] Message);

    private byte[] CreateChatJson(long no, int vpos, DateTimeOffset now, string userId, string mail, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WritePropertyName("chat");
        writer.WriteStartObject();
        writer.WriteString("thread", _channel);
        writer.WriteNumber("no", no);
        writer.WriteNumber("vpos", vpos);
        writer.WriteNumber("date", now.ToUnixTimeSeconds());
        writer.WriteNumber("date_usec", (now.ToUnixTimeMilliseconds() % 1000) * 1000);
        writer.WriteString("user_id", userId);
        writer.WriteNumber("premium", 0);
        writer.WriteNumber("anonymity", 1);
        if (!string.IsNullOrEmpty(mail))
            writer.WriteString("mail", mail);
        writer.WriteString("content", text);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return ms.ToArray();
    }

    private static string BuildMail(JsonElement data)
    {
        var parts = new List<string>();
        AddString(data, parts, "color");
        AddString(data, parts, "position");
        AddString(data, parts, "size");
        AddString(data, parts, "font");
        return string.Join(' ', parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static void AddString(JsonElement data, List<string> parts, string name)
    {
        if (data.TryGetProperty(name, out var value) && value.GetString() is { } text)
            parts.Add(text);
    }

    private static string NormalizeComment(string text) =>
        text.Replace("\r", "").Replace("\n", " ").Trim();

    private static string NormalizeUserId(string userId)
    {
        userId = userId.Trim();
        if (userId.Length > 32) userId = userId[..32];
        return userId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_') ? userId : "";
    }

    private static string NormalizeThreadId(string channel) =>
        channel.EndsWith("r", StringComparison.Ordinal) &&
        channel.Length > 2 &&
        channel[0] == 'j' &&
        channel[1] == 'k' &&
        channel.AsSpan(2, channel.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0
            ? channel[..^1]
            : channel;

    private static ReadOnlyMemory<byte> NormalizeThread(ReadOnlyMemory<byte> message, string threadId)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("chat", out var chat) ||
                !chat.TryGetProperty("thread", out var threadElement) ||
                threadElement.GetString() == threadId)
                return message;

            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            writer.WritePropertyName("chat");
            writer.WriteStartObject();
            foreach (var prop in chat.EnumerateObject())
            {
                if (prop.Name == "thread")
                    writer.WriteString("thread", threadId);
                else
                    prop.WriteTo(writer);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            return ms.ToArray().AsMemory();
        }
        catch
        {
            return message;
        }
    }

    private static byte[] Error(string code) =>
        Encoding.UTF8.GetBytes("{\"type\":\"error\",\"data\":{\"code\":\"" + code + "\"}}");

    private sealed class LocalClient
    {
        private readonly Queue<byte[]> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly int _queueLimit;
        private readonly int _dropDisconnectStreak;
        private readonly TimeSpan _dropStreakReset;
        private readonly MetricsService _metrics;
        private bool _completed;
        private int _dropStreak;
        private DateTimeOffset _lastDropUtc = DateTimeOffset.MinValue;
        private DateTimeOffset _lastSentUtc = DateTimeOffset.MinValue;

        public Guid Id { get; }
        public WebSocket WebSocket { get; }
        public string ThreadId { get; }

        public LocalClient(Guid id, WebSocket webSocket, int queueLimit, int dropDisconnectStreak,
            TimeSpan dropStreakReset, string threadId, MetricsService metrics)
        {
            Id = id;
            WebSocket = webSocket;
            _queueLimit = queueLimit;
            _dropDisconnectStreak = dropDisconnectStreak;
            _dropStreakReset = dropStreakReset;
            ThreadId = threadId;
            _metrics = metrics;
        }

        public bool Enqueue(byte[] message)
        {
            lock (_queue)
            {
                if (_completed) return false;
                if (_queue.Count >= _queueLimit)
                {
                    var dropped = _queue.Count;
                    _queue.Clear();
                    _metrics.RecordQueueDrop(dropped);
                    if (DateTimeOffset.UtcNow - _lastDropUtc > _dropStreakReset)
                        _dropStreak = 0;
                    _dropStreak++;
                    _lastDropUtc = DateTimeOffset.UtcNow;
                }
                _queue.Enqueue(message);
                _signal.Release();
                return _dropStreak >= _dropDisconnectStreak;
            }
        }

        public async Task<ArraySegment<byte>?> DequeueAsync(CancellationToken ct)
        {
            await _signal.WaitAsync(ct);
            lock (_queue)
            {
                if (_queue.Count == 0) return null;
                return new ArraySegment<byte>(_queue.Dequeue());
            }
        }

        public void MarkSent()
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastSentUtc > _dropStreakReset)
                _dropStreak = 0;
            _lastSentUtc = now;
        }

        public void Complete()
        {
            lock (_queue)
            {
                _completed = true;
                _queue.Clear();
                _signal.Release();
            }
        }
    }
}
