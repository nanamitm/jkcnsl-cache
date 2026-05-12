using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;

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
    private readonly Queue<long> _recentMs = new();
    private readonly object _statsLock = new();

    public bool IsMonitored { get; set; }
    public bool IsRunning { get { lock (this) { return _running; } } }
    public virtual string? CurrentTarget => null;
    public virtual bool IsScheduled => false;
    public virtual DateTimeOffset? ScheduledStartUtc => null;
    public virtual DateTimeOffset? VposBaseTime => null;
    public virtual string Status => IsRunning ? "running" : "idle";
    public virtual string? StatusText => null;
    // NicoNico の watch ページスクレイピングに使う ID（ch??? / lv??? など）
    public virtual string? WatchTarget => null;
    public virtual string GetDownstreamThreadId(string requestedChannel) => requestedChannel;

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

    protected UpstreamChannelBase(string channel, ILogger logger, MetricsService? metrics = null)
    {
        _channel = channel;
        _logger = logger;
        _metrics = metrics;
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

    public virtual async Task AddClientAndWaitAsync(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = ws;

        var buf = new byte[256];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buf, ct);
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

    private async Task RunLoopAsync(CancellationToken ct)
    {
        int delaySec = 10;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndReceiveAsync(ct);
                // 正常切断はすぐ再接続（放送終了→新放送など）、バックオフもリセット
                delaySec = 10;
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
                var jitterSec = Random.Shared.Next(-delaySec / 2, delaySec / 2);
                var actualDelay = Math.Max(5, delaySec + jitterSec);
                _logger.LogWarning(ex, "[{Channel}] 上流切断。{Delay}秒後に再接続します", _channel, actualDelay);
                try { await Task.Delay(actualDelay * 1000, ct); }
                catch (OperationCanceledException) { break; }
                // 最大5分まで指数バックオフ
                delaySec = Math.Min(delaySec * 2, 300);
            }
        }
    }

    protected abstract Task ConnectAndReceiveAsync(CancellationToken ct);

    protected static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);
}
