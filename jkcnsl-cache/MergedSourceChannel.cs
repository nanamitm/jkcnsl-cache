using System.Text.Json;
using System.Threading.Channels;

namespace jkcnsl_cache;

// 統合ソース（jk{ID}m）: 避難所（jk{ID}r）と 5ch（jk{ID}n）を 1 本のコメントストリームにまとめて配信する。
// NicoJK のように避難所を 1 つしか持てないクライアントでも、両方のコメントを受け取れるようにするためのもの。
// 公式（jk{ID}）はクライアントが別途受けているので混ぜない（混ぜると二重に流れる）。
//
// 上流には自分で接続せず、既存の避難所・5ch チャンネルをプロセス内で購読する（上流への接続数や 5ch の取得回数は増えない）。
// 2 つのソースの no が重ならないよう no は振り直し、vpos はこのチャンネルの vposBaseTime 基準に直す。
// thread はそのまま（どちらも部屋の threadId に揃っており、避難所が転送してきた他所のコメントの thread も変えない）。
public sealed class MergedSourceChannel : UpstreamChannelBase
{
    private const string RefugeSource = "refuge";
    private const string NichanSource = "nichan";
    // コメント受信者がいない状態がこれだけ続いたら自分から止まる
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);

    private readonly string _threadId;
    private readonly string _refugeKey;
    private readonly string _nichanKey;
    private readonly Func<string, UpstreamChannelBase> _acquire;
    private readonly Action<string> _release;
    private readonly TimeSpan _historyDuration;
    private readonly int _historyLimit;
    private readonly long _vposBaseTicks = DateTimeOffset.UtcNow.UtcTicks;
    private readonly object _lock = new();
    private readonly Queue<(DateTimeOffset At, byte[] Message)> _history = new();
    // 避難所が接続直後に送る制御メッセージ（ping ps → thread → ping pf）。届いた順のまま持つ
    private readonly List<byte[]> _refugeControlMessages = new();
    private byte[]? _nichanThreadsMessage;
    private long _nextNo;
    private string _status = "idle";

    private readonly Channel<(string Source, UpstreamChannelBase From, byte[] Message)> _queue =
        Channel.CreateBounded<(string, UpstreamChannelBase, byte[])>(new BoundedChannelOptions(4096)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

    public override string Status { get { lock (_lock) return IsRunning ? _status : "idle"; } }
    public override string? CurrentTarget => IsRunning ? $"{_refugeKey}+{_nichanKey}" : null;
    public override DateTimeOffset? VposBaseTime => new DateTimeOffset(_vposBaseTicks, TimeSpan.Zero);
    public override string GetDownstreamThreadId(string requestedChannel) => _threadId;

    public MergedSourceChannel(string channel, string threadId, string refugeKey, string nichanKey,
        Func<string, UpstreamChannelBase> acquire, Action<string> release,
        TimeSpan historyDuration, int historyLimit,
        IConfiguration config, ILogger logger, MetricsService? metrics)
        : base(channel, logger, metrics, config)
    {
        _threadId = threadId;
        _refugeKey = refugeKey;
        _nichanKey = nichanKey;
        _acquire = acquire;
        _release = release;
        _historyDuration = historyDuration;
        _historyLimit = historyLimit;
    }

    // 投稿は避難所へ（5ch は書き込めない）。避難所がローカル待避中なら、避難所側がローカルコメントとして処理する。
    // クライアントの vpos はこのチャンネルの vposBaseTime 基準なので、避難所の基準に直してから渡す
    // （届いたコメントは ConvertVpos で戻すので、投稿した vpos のまま流れる）
    public override Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        var refuge = _acquire(_refugeKey);
        return refuge.PostCommentAsync(ConvertPostVpos(json, refuge.VposBaseTime), ct);
    }

    private ReadOnlyMemory<byte> ConvertPostVpos(ReadOnlyMemory<byte> json, DateTimeOffset? refugeBase)
    {
        if (refugeBase is not { } toBase) return json;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("vpos", out var vposElement) || !vposElement.TryGetInt64(out var vpos))
                return json;
            var converted = Math.Clamp(vpos + (_vposBaseTicks - toBase.UtcTicks) / TimeSpan.TicksPerMillisecond / 10,
                0, int.MaxValue);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "data")
                    {
                        prop.WriteTo(writer);
                        continue;
                    }
                    writer.WritePropertyName("data");
                    writer.WriteStartObject();
                    foreach (var field in data.EnumerateObject())
                    {
                        if (field.Name == "vpos")
                            writer.WriteNumber("vpos", converted);
                        else
                            field.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            return ms.ToArray();
        }
        catch (JsonException)
        {
            return json;
        }
    }

    // 新しいクライアントには、避難所の直近の制御メッセージ（ping ps・thread 情報・ping pf。避難所単体と同じ並び）・
    // 5ch のスレ一覧・5ch の直近のレスを送る
    // （避難所単体のクライアントにも過去コメントは送っていないので、避難所のコメントは履歴に含めない）
    protected override IReadOnlyList<ReadOnlyMemory<byte>> GetInitialMessages()
    {
        lock (_lock)
        {
            TrimHistory(DateTimeOffset.UtcNow);
            var messages = new List<ReadOnlyMemory<byte>>(_refugeControlMessages.Count + _history.Count + 1);
            messages.AddRange(_refugeControlMessages.Select(m => (ReadOnlyMemory<byte>)m));
            if (_nichanThreadsMessage != null) messages.Add(_nichanThreadsMessage);
            messages.AddRange(_history.Select(h => (ReadOnlyMemory<byte>)h.Message));
            return messages;
        }
    }

    protected override async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        // 統合ソースの視聴者は避難所側の視聴者として数える（両方に数えるとチャンネルの合計が二重になる）
        var refuge = new SourceLink(this, RefugeSource, _refugeKey, countViewers: true);
        var nichan = new SourceLink(this, NichanSource, _nichanKey, countViewers: false);
        try
        {
            lock (_lock) _status = "running";
            refuge.Refresh();
            nichan.Refresh();
            _logger.LogInformation("[{Channel}] 統合ソースを開始: {Refuge} + {Nichan}", _channel, _refugeKey, _nichanKey);

            // 購読先のチャンネルが作り直されたり止まったりしていないか、ときどき確かめる
            var refreshTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
            Task<bool>? readTask = null;
            var lastClientSeen = DateTimeOffset.UtcNow;
            while (!ct.IsCancellationRequested)
            {
                // 単一読み手のチャンネルなので、待ち受けは常に 1 つだけにする
                readTask ??= _queue.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(readTask, refreshTask);
                if (completed == refreshTask)
                {
                    await refreshTask;
                    refreshTask = Task.Delay(TimeSpan.FromSeconds(15), ct);
                    // 受信者がいないまま動き続けると、元ソースを購読したままになり（元ソースからは受信者 1 に見える）、
                    // 5ch の取得も止まらない。止め損ねがあってもここで自分から止まる
                    var now = DateTimeOffset.UtcNow;
                    // 監視専用チャンネルは受信者がいなくても動かし続ける（_release も止めない）
                    if (ClientCount > 0 || IsMonitored)
                        lastClientSeen = now;
                    else if (now - lastClientSeen > IdleAfter)
                    {
                        _logger.LogInformation("[{Channel}] 受信者がいないため統合ソースを停止します", _channel);
                        _release(_channel);
                        continue;
                    }
                    refuge.Refresh();
                    nichan.Refresh();
                    continue;
                }
                var hasData = await readTask;
                readTask = null;
                if (!hasData) break;
                while (_queue.Reader.TryRead(out var item))
                {
                    var output = Transform(item.Source, item.From, item.Message);
                    if (output != null)
                        await BroadcastAsync(output);
                }
            }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_lock) _status = "idle";
            refuge.Dispose();
            nichan.Dispose();
            // 統合ソースのためだけに動かしていたチャンネルは、受信者がいなければ止める
            _release(_refugeKey);
            _release(_nichanKey);
        }
    }

    private byte[]? Transform(string source, UpstreamChannelBase from, byte[] message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                return null;
            var now = DateTimeOffset.UtcNow;
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                if (root.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in root.EnumerateArray())
                        WriteItem(writer, source, from, item);
                    writer.WriteEndArray();
                }
                else
                {
                    WriteItem(writer, source, from, root);
                }
            }
            var output = ms.ToArray();

            // 新しく来たクライアントに渡すための保存
            if (root.ValueKind == JsonValueKind.Object)
            {
                lock (_lock)
                {
                    if (source == RefugeSource && GetRefugeControlKind(root) is { } kind)
                        StoreRefugeControlMessage(kind, output);
                    else if (source == NichanSource && root.TryGetProperty("nichan_threads", out _))
                        _nichanThreadsMessage = output;
                    else if (source == NichanSource && root.TryGetProperty("chat", out _))
                    {
                        _history.Enqueue((now, output));
                        TrimHistory(now);
                    }
                }
            }
            return output;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private enum RefugeControlKind { PingStart, Thread, PingFinish }

    private static RefugeControlKind? GetRefugeControlKind(JsonElement root)
    {
        if (root.TryGetProperty("thread", out _)) return RefugeControlKind.Thread;
        if (root.TryGetProperty("ping", out var ping) && ping.ValueKind == JsonValueKind.Object &&
            ping.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            var text = content.GetString()!;
            if (text.StartsWith("ps:", StringComparison.Ordinal)) return RefugeControlKind.PingStart;
            if (text.StartsWith("pf:", StringComparison.Ordinal)) return RefugeControlKind.PingFinish;
        }
        return null;
    }

    // ping ps か、ps を伴わない 2 つ目の thread が来たら、避難所がつなぎ直したものとして持ち直す。_lock を取って呼ぶ
    private void StoreRefugeControlMessage(RefugeControlKind kind, byte[] message)
    {
        if (kind == RefugeControlKind.PingStart ||
            (kind == RefugeControlKind.Thread && _refugeControlMessages.Count > 0 &&
             GetStoredKind(_refugeControlMessages[^1]) != RefugeControlKind.PingStart))
            _refugeControlMessages.Clear();
        else if (kind == RefugeControlKind.PingFinish &&
                 (_refugeControlMessages.Count == 0 ||
                  GetStoredKind(_refugeControlMessages[^1]) == RefugeControlKind.PingFinish))
            return;
        _refugeControlMessages.Add(message);
    }

    private static RefugeControlKind? GetStoredKind(byte[] message)
    {
        using var doc = JsonDocument.Parse(message);
        return GetRefugeControlKind(doc.RootElement);
    }

    private void WriteItem(Utf8JsonWriter writer, string source, UpstreamChannelBase from, JsonElement item)
    {
        // 避難所の thread 情報の last_res は避難所側の番号なので、振り直した番号に合わせる
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("thread", out var thread) &&
            thread.ValueKind == JsonValueKind.Object && thread.TryGetProperty("last_res", out _))
        {
            writer.WriteStartObject();
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name != "thread")
                {
                    prop.WriteTo(writer);
                    continue;
                }
                writer.WritePropertyName("thread");
                writer.WriteStartObject();
                foreach (var field in thread.EnumerateObject())
                {
                    if (field.Name == "last_res")
                        writer.WriteNumber("last_res", Interlocked.Read(ref _nextNo));
                    else
                        field.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            return;
        }

        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("chat", out var chat) ||
            chat.ValueKind != JsonValueKind.Object)
        {
            item.WriteTo(writer);
            return;
        }

        var no = Interlocked.Increment(ref _nextNo);
        RecordComment(no);
        var vpos = ConvertVpos(from, chat);
        writer.WriteStartObject();
        foreach (var prop in item.EnumerateObject())
        {
            if (prop.Name != "chat")
            {
                prop.WriteTo(writer);
                continue;
            }
            writer.WritePropertyName("chat");
            writer.WriteStartObject();
            foreach (var field in chat.EnumerateObject())
            {
                if (field.Name is "no" or "vpos" or "source") continue;
                field.WriteTo(writer);
            }
            writer.WriteNumber("no", no);
            writer.WriteNumber("vpos", vpos);
            // どちらのソースのコメントか（避難所互換クライアントは無視する）
            writer.WriteString("source", source);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    // 元ソースの vposBaseTime が分かればそのずれを足し、分からなければ投稿時刻から求める
    private int ConvertVpos(UpstreamChannelBase from, JsonElement chat)
    {
        long vposMs;
        if (from.VposBaseTime is { } fromBase &&
            chat.TryGetProperty("vpos", out var vposElement) && vposElement.TryGetInt64(out var vpos))
        {
            vposMs = vpos * 10 + (fromBase.UtcTicks - _vposBaseTicks) / TimeSpan.TicksPerMillisecond;
        }
        else
        {
            var at = DateTimeOffset.UtcNow;
            if (chat.TryGetProperty("date", out var dateElement) && dateElement.TryGetInt64(out var date))
            {
                var usec = chat.TryGetProperty("date_usec", out var usecElement) && usecElement.TryGetInt64(out var u) ? u : 0;
                at = DateTimeOffset.FromUnixTimeSeconds(date).AddTicks(usec * 10);
            }
            vposMs = (at.UtcTicks - _vposBaseTicks) / TimeSpan.TicksPerMillisecond;
        }
        return (int)Math.Clamp(vposMs / 10, 0, int.MaxValue);
    }

    private void TrimHistory(DateTimeOffset now)
    {
        while (_history.Count > _historyLimit ||
               (_history.Count > 0 && now - _history.Peek().At > _historyDuration))
            _history.Dequeue();
    }

    // 1 つの元ソースへの購読。元チャンネルが作り直されていたら購読し直す
    private sealed class SourceLink(MergedSourceChannel owner, string source, string key, bool countViewers) : IDisposable
    {
        private UpstreamChannelBase? _channel;
        private IDisposable? _subscription;

        public void Refresh()
        {
            UpstreamChannelBase current;
            try { current = owner._acquire(key); }
            catch (Exception ex)
            {
                owner._logger.LogWarning(ex, "[{Channel}] 統合元 {Key} を開始できませんでした", owner._channel, key);
                return;
            }
            if (ReferenceEquals(current, _channel) && _subscription != null) return;
            _subscription?.Dispose();
            _channel = current;
            _subscription = current.Subscribe(
                message => owner._queue.Writer.TryWrite((source, current, message.ToArray())),
                () => countViewers ? owner.ClientCount : 0);
        }

        public void Dispose()
        {
            _subscription?.Dispose();
            _subscription = null;
            _channel = null;
        }
    }
}
