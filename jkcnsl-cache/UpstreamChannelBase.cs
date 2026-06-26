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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _clientSendLocks = new();
    private readonly TimeSpan _broadcastSendTimeout;
    private CancellationTokenSource _cts = new();
    private Task _runTask = Task.CompletedTask;
    private bool _running = false;

    private long _totalComments = 0;
    private long _lastResNo = 0;
    private long _fallbackNextNo = 0;
    private long _fallbackVposBaseTicks = DateTimeOffset.UtcNow.UtcTicks;
    private volatile bool _localFallbackActive = false;
    private int _reconnectDelaySec = 10;
    private long _connectionAttemptSeq = 0;
    private long _currentConnectionAttemptId = 0;
    private readonly Queue<long> _recentMs = new();
    private readonly object _statsLock = new();

    public bool IsMonitored { get; set; }
    public CommentStorageService? CommentStorage { get; set; }
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
    // ローカル待避が現在の状態に入った時刻（待避中でなければ null）。watchdog の長時間待避検出に使う。
    public DateTimeOffset? LocalFallbackSinceUtc =>
        _localFallbackActive ? new DateTimeOffset(Interlocked.Read(ref _fallbackVposBaseTicks), TimeSpan.Zero) : null;
    public virtual string Status => IsRunning ? "running" : "idle";
    public virtual string? StatusText => null;
    // NicoNico の watch ページスクレイピングに使う ID（ch??? / lv??? など）
    public virtual string? WatchTarget => null;
    public virtual string GetDownstreamThreadId(string requestedChannel) => NormalizeJkThreadId(requestedChannel);
    protected long CurrentConnectionAttemptId => Interlocked.Read(ref _currentConnectionAttemptId);

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
        _broadcastSendTimeout = TimeSpan.FromSeconds(Math.Max(1,
            config?.GetValue<int>("CacheServer:BroadcastSendTimeoutSeconds", 5) ?? 5));
    }

    protected void SetLocalFallbackActive(bool active)
    {
        var previous = _localFallbackActive;
        if (active && !previous)
            Interlocked.Exchange(ref _fallbackVposBaseTicks, DateTimeOffset.UtcNow.UtcTicks);
        _localFallbackActive = active;
        if (previous != active)
        {
            _logger.LogInformation(
                "[{Channel}] fallback状態変更: from={From} to={To} reason={Reason} attempt={Attempt} status={Status} currentTarget={CurrentTarget} clients={ClientCount} lastResNo={LastResNo}",
                _channel,
                previous,
                active,
                GetFallbackChangeReason(),
                CurrentConnectionAttemptId,
                Status,
                CurrentTarget ?? "(null)",
                ClientCount,
                LastResNo);
        }
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
            _logger.LogDebug(
                "[{Channel}] fallbackコメント投稿: attempt={Attempt} no={No} vpos={Vpos} userId={UserId} textLength={TextLength}",
                _channel, CurrentConnectionAttemptId, no, vpos, userId, text.Length);
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
        // RunLoopAsync は fire-and-forget のため、内部の try/catch をすり抜けた
        // 想定外の例外（フィルタ内の例外など）が起きてもログに残し、UnobservedTaskException を防ぐ。
        _runTask.ContinueWith(t =>
            _logger.LogCritical(t.Exception, "[{Channel}] 上流接続ループが想定外の例外で停止しました", _channel),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // watchdog 用: 実行中のループを止めて再起動する。fallbackLocal に固まって復帰しないチャンネルの応急処置。
    public async Task RestartAsync()
    {
        await StopAsync();
        EnsureRunning();
    }

    // コメントクライアントが何も送ってこない場合の最大待ち時間
    // （ネットワーク機器/NAT 経路でゾンビ化した接続を検出するため）
    private readonly TimeSpan _commentIdleTimeout;
    protected virtual TimeSpan CommentIdleTimeout => _commentIdleTimeout;

    public virtual async Task AddClientAndWaitAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;
        _clientSendLocks[id] = new SemaphoreSlim(1, 1);

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
        _clientSendLocks.TryRemove(id, out _);

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
        if (CommentStorage != null && message.Span.StartsWith("{\"chat\""u8))
            CommentStorage.TryEnqueue(_channel, message);

        var tasks = _clients.ToArray()
            .Where(kv => kv.Value.State == WebSocketState.Open)
            .Select(async kv =>
            {
                try
                {
                    using var timeoutCts = new CancellationTokenSource(_broadcastSendTimeout);
                    if (!_clientSendLocks.TryGetValue(kv.Key, out var sendLock))
                        return;
                    await sendLock.WaitAsync(timeoutCts.Token);
                    try
                    {
                        await kv.Value.SendAsync(message, WebSocketMessageType.Text, true, timeoutCts.Token);
                        _metrics?.RecordDelivered();
                    }
                    finally
                    {
                        sendLock.Release();
                    }
                }
                catch
                {
                    if (_clients.TryRemove(kv.Key, out var ws) &&
                        ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                                "broadcast send timeout", CancellationToken.None);
                        }
                        catch { }
                    }
                }
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
        try
        {
            await RunLoopCoreAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // ここに到達するのは内側の catch(Exception) で想定していない例外（ログ処理中の例外等）。
            // 原因調査のため記録し、_running を確実に false に戻して EnsureRunning() での再起動を可能にする。
            _logger.LogCritical(ex, "[{Channel}] 上流接続ループが予期せず停止しました", _channel);
        }
        finally
        {
            lock (this) { _running = false; }
        }
    }

    private async Task RunLoopCoreAsync(CancellationToken ct)
    {
        ResetReconnectBackoff();
        while (!ct.IsCancellationRequested)
        {
            var attemptId = Interlocked.Increment(ref _connectionAttemptSeq);
            Interlocked.Exchange(ref _currentConnectionAttemptId, attemptId);
            var logRepeatedFallbackAttempt = IsLocalFallbackActive &&
                (Status == "fallbackLocal" || Status == "retryWaiting");
            if (logRepeatedFallbackAttempt)
            {
                _logger.LogDebug(
                    "[{Channel}] 上流接続試行を開始: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget} watchTarget={WatchTarget}",
                    _channel,
                    attemptId,
                    IsLocalFallbackActive,
                    Status,
                    CurrentTarget ?? "(null)",
                    WatchTarget ?? "(null)");
            }
            else
            {
                _logger.LogInformation(
                    "[{Channel}] 上流接続試行を開始: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget} watchTarget={WatchTarget}",
                    _channel,
                    attemptId,
                    IsLocalFallbackActive,
                    Status,
                    CurrentTarget ?? "(null)",
                    WatchTarget ?? "(null)");
            }
            try
            {
                await ConnectAndReceiveAsync(ct);
                if (logRepeatedFallbackAttempt)
                {
                    _logger.LogDebug(
                        "[{Channel}] 上流接続試行が終了: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget}",
                        _channel,
                        attemptId,
                        IsLocalFallbackActive,
                        Status,
                        CurrentTarget ?? "(null)");
                }
                else
                {
                    _logger.LogInformation(
                        "[{Channel}] 上流接続試行が終了: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget}",
                        _channel,
                        attemptId,
                        IsLocalFallbackActive,
                        Status,
                        CurrentTarget ?? "(null)");
                }
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
                    _logger.LogWarning(
                        "[{Channel}] 上流から403が返りました。1時間後に再試行します: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget}",
                        _channel, attemptId, IsLocalFallbackActive, Status, CurrentTarget ?? "(null)");
                    try { await Task.Delay(TimeSpan.FromHours(1), ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }
                // ±50% のジッターを加えてチャンネル間の再接続タイミングを分散
                var delaySec = Volatile.Read(ref _reconnectDelaySec);
                var jitterSec = Random.Shared.Next(-delaySec / 2, delaySec / 2);
                var actualDelay = Math.Max(5, delaySec + jitterSec);
                _logger.LogWarning(
                    ex,
                    "[{Channel}] 上流切断。{Delay}秒後に再接続します: attempt={Attempt} fallback={Fallback} status={Status} currentTarget={CurrentTarget}",
                    _channel,
                    actualDelay,
                    attemptId,
                    IsLocalFallbackActive,
                    Status,
                    CurrentTarget ?? "(null)");
                try { await Task.Delay(actualDelay * 1000, ct); }
                catch (OperationCanceledException) { break; }
                // 最大5分まで指数バックオフ
                Interlocked.Exchange(ref _reconnectDelaySec, Math.Min(delaySec * 2, 300));
            }
        }
    }

    protected virtual string GetFallbackChangeReason() => "state_transition";

    protected abstract Task ConnectAndReceiveAsync(CancellationToken ct);

    protected static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
}
