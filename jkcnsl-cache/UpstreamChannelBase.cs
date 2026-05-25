using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace jkcnsl_cache;

public abstract class UpstreamChannelBase
{
    protected readonly string _channel;
    protected readonly ILogger _logger;
    private readonly MetricsService? _metrics;
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private CancellationTokenSource _cts = new();
    private Task _runTask = Task.CompletedTask;
    private bool _running = false;

    private long _totalComments = 0;
    private long _lastResNo = 0;
    private long _fallbackNextNo = 0;
    private long _fallbackVposBaseTicks = DateTimeOffset.UtcNow.UtcTicks;
    private volatile bool _localFallbackActive = false;
    private int _reconnectDelaySec = 10;
    private readonly Queue<long> _recentMs = new();
    private readonly object _statsLock = new();

    public bool IsMonitored { get; set; }
    public bool IsRunning { get { lock (this) { return _running; } } }
    public virtual string? CurrentTarget => null;
    public virtual bool IsScheduled => false;
    public virtual DateTimeOffset? ScheduledStartUtc => null;
    public virtual DateTimeOffset? VposBaseTime
    {
        get
        {
            if (!IsLocalFallbackActive) return null;
            var ticks = Interlocked.Read(ref _fallbackVposBaseTicks);
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
    public bool IsLocalFallbackActive => _localFallbackActive;
    public virtual string Status => IsRunning ? "running" : "idle";
    public virtual string? StatusText => null;
    // NicoNico の watch ページスクレイピングに使う ID（ch??? / lv??? など）
    public virtual string? WatchTarget => null;
    public virtual string GetDownstreamThreadId(string requestedChannel) => NormalizeJkThreadId(requestedChannel);

    public virtual Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
        => Task.FromResult<ReadOnlyMemory<byte>>(
            "{\"type\":\"error\",\"data\":{\"code\":\"NOT_CONNECTED\"}}"u8.ToArray());
    public virtual int ClientCount => _clients.Values.Count(c => c.State == WebSocketState.Open);
    public virtual Task AddClientAndWaitAsync(WebSocket ws, string requestedChannel, CancellationToken ct) =>
        AddClientAndWaitAsync(ws, ct);
    public long TotalComments => Interlocked.Read(ref _totalComments);
    public long LastResNo => Interlocked.Read(ref _lastResNo);
    public int Force
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
            lock (_statsLock)
            {
                while (_recentMs.Count > 0 && _recentMs.Peek() < cutoff)
                    _recentMs.Dequeue();
                return _recentMs.Count;
            }
        }
    }

    protected UpstreamChannelBase(string channel, ILogger logger, MetricsService? metrics = null,
        IConfiguration? config = null)
    {
        _channel = channel;
        _logger = logger;
        _metrics = metrics;
        _commentIdleTimeout = TimeSpan.FromSeconds(Math.Max(30,
            config?.GetValue<int>("CacheServer:CommentIdleTimeoutSeconds", 600) ?? 600));
    }

    protected void SetLocalFallbackActive(bool active)
    {
        if (active && !_localFallbackActive)
            Interlocked.Exchange(ref _fallbackVposBaseTicks, DateTimeOffset.UtcNow.UtcTicks);
        _localFallbackActive = active;
    }

    protected void ResetReconnectBackoff() =>
        Interlocked.Exchange(ref _reconnectDelaySec, 10);

    protected Task<ReadOnlyMemory<byte>> PostLocalFallbackCommentAsync(
        ReadOnlyMemory<byte> json, string? localUserId, int maxCommentLength, CancellationToken ct)
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
            if (text.Length > maxCommentLength)
                return Task.FromResult<ReadOnlyMemory<byte>>(Error("COMMENT_TOO_LONG"));

            var no = Interlocked.Increment(ref _fallbackNextNo);
            var vpos = data.TryGetProperty("vpos", out var vposElement) && vposElement.TryGetInt32(out var parsedVpos)
                ? Math.Max(0, parsedVpos)
                : CalcFallbackVpos();
            var mail = BuildLocalMail(data);
            var now = DateTimeOffset.UtcNow;
            var userId = NormalizeLocalUserId(localUserId ?? "");
            if (string.IsNullOrEmpty(userId) &&
                data.TryGetProperty("localUserId", out var userIdElement))
                userId = NormalizeLocalUserId(userIdElement.GetString() ?? "");
            if (string.IsNullOrEmpty(userId))
                userId = $"local-{Random.Shared.NextInt64(0x100000000L):x8}";
            var chatJson = CreateLocalChatJson(no, vpos, now, userId, mail, text);
            RecordComment(no);
            _ = BroadcastAsync(chatJson);
            return Task.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(
                "{\"type\":\"postCommentResult\",\"data\":{\"status\":\"ok\",\"no\":" + no + "}}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[{Channel}] fallback localstream 投稿処理に失敗しました", _channel);
            return Task.FromResult<ReadOnlyMemory<byte>>(Error("INTERNAL_ERROR"));
        }
    }

    public void EnsureRunning()
    {
        lock (this)
        {
            if (_running) return;
            _cts = new CancellationTokenSource();
            _runTask = RunLoopAsync(_cts.Token);
            _running = true;
        }
    }

    // コメントクライアントが何も送ってこない場合の最大待ち時間
    // （ネットワーク機器/NAT 経路でゾンビ化した接続を検出するため）
    private readonly TimeSpan _commentIdleTimeout;
    protected virtual TimeSpan CommentIdleTimeout => _commentIdleTimeout;

    public virtual async Task AddClientAndWaitAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;

        var buf = new byte[256];
        var idleTimeout = CommentIdleTimeout;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(idleTimeout);
                WebSocketReceiveResult result;
                try
                {
                    result = await ws.ReceiveAsync(buf, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("[{Channel}] コメントセッションがアイドルタイムアウトしました: timeoutSeconds={T}",
                        _channel, (int)idleTimeout.TotalSeconds);
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (InvalidOperationException) { }

        _clients.TryRemove(id, out _);

        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    public async Task StopAsync()
    {
        lock (this) { _running = false; }
        await _cts.CancelAsync();
        await _runTask;
    }

    protected void RecordComment(long no)
    {
        Interlocked.Increment(ref _totalComments);
        long current = Interlocked.Read(ref _lastResNo);
        while (no > current && Interlocked.CompareExchange(ref _lastResNo, no, current) != current)
            current = Interlocked.Read(ref _lastResNo);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        lock (_statsLock)
            _recentMs.Enqueue(now);
    }

    protected async Task BroadcastAsync(ReadOnlyMemory<byte> message)
    {
        var tasks = _clients.Values
            .Where(c => c.State == WebSocketState.Open)
            .Select(async c =>
            {
                try
                {
                    await c.SendAsync(message, WebSocketMessageType.Text, true, CancellationToken.None);
                    _metrics?.RecordDelivered();
                }
                catch { }
            });
        await Task.WhenAll(tasks);
    }

    private int CalcFallbackVpos()
    {
        var baseTicks = Interlocked.Read(ref _fallbackVposBaseTicks);
        if (baseTicks <= 0) return 0;
        var baseTime = new DateTimeOffset(baseTicks, TimeSpan.Zero);
        return Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - baseTime).TotalMilliseconds / 10));
    }

    private byte[] CreateLocalChatJson(long no, int vpos, DateTimeOffset now, string userId, string mail, string text)
    {
        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();
        writer.WritePropertyName("chat");
        writer.WriteStartObject();
        writer.WriteString("thread", GetDownstreamThreadId(_channel));
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

    private static string BuildLocalMail(JsonElement data)
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

    private static string NormalizeLocalUserId(string userId)
    {
        userId = userId.Trim();
        if (userId.Length > 32) userId = userId[..32];
        return userId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_') ? userId : "";
    }

    protected static string NormalizeJkThreadId(string channel) =>
        channel.EndsWith("r", StringComparison.Ordinal) &&
        channel.Length > 2 &&
        channel[0] == 'j' &&
        channel[1] == 'k' &&
        channel.AsSpan(2, channel.Length - 3).IndexOfAnyExceptInRange('0', '9') < 0
            ? channel[..^1]
            : channel;

    private static byte[] Error(string code) =>
        Encoding.UTF8.GetBytes("{\"type\":\"error\",\"data\":{\"code\":\"" + code + "\"}}");

    private async Task RunLoopAsync(CancellationToken ct)
    {
        ResetReconnectBackoff();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(ct);
                // 正常切断はすぐ再接続（放送終了→新放送など）、バックオフもリセット
                ResetReconnectBackoff();
                try { await Task.Delay(5_000, ct); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                // 403 は「接続拒否」のため長時間待機（1時間）
                if (ex.Message.Contains("403"))
                {
                    _logger.LogWarning("[{Channel}] 上流から403が返りました。1時間後に再試行します", _channel);
                    try { await Task.Delay(TimeSpan.FromHours(1), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                // ±50% のジッターを加えてチャンネル間の再接続タイミングを分散
                var delaySec = Volatile.Read(ref _reconnectDelaySec);
                var jitterSec = Random.Shared.Next(-delaySec / 2, delaySec / 2);
                var actualDelay = Math.Max(5, delaySec + jitterSec);
                _logger.LogWarning(ex, "[{Channel}] 上流切断。{Delay}秒後に再接続します", _channel, actualDelay);
                try { await Task.Delay(actualDelay * 1000, ct); }
                catch (OperationCanceledException) { break; }
                // 最大5分まで指数バックオフ
                Interlocked.Exchange(ref _reconnectDelaySec, Math.Min(delaySec * 2, 300));
            }
        }
    }

    protected abstract Task ConnectAndReceiveAsync(CancellationToken ct);

    protected static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
}
