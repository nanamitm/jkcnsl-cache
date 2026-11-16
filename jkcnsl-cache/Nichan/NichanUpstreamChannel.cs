using System.Text.Json;

namespace jkcnsl_cache.Nichan;

// 5ch 実況スレを上流とするチャンネル（appsettings の値が "5ch:" / "5ch:{read.cgi URL}"）。
// subject.txt からスレを選び、dat を差分取得し、レスを避難所互換の chat JSON にして配信する。
// レスは「書き込み時刻 + PlaybackDelay」に配信し、ポーリングでまとめて届いたレスを書き込み時の間隔で流す。
public sealed class NichanUpstreamChannel : UpstreamChannelBase
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    // Force（直近 1 分の配信数）が 1 分ぶんたまるまでの時間
    private static readonly TimeSpan ForceWarmUp = TimeSpan.FromMinutes(1);

    private readonly NichanOptions _options;
    private readonly NichanHttpClient _http;
    private readonly NichanBoardService _boards;
    private readonly NichanBoardCatalog _catalog;
    // 番組表による現在の番組（スレ選択で番組名に一致するスレを優先する）
    private readonly NichanProgramTracker _program;
    private readonly NichanThreadAddress? _fixedThread;
    private readonly string? _invalidTarget;
    private readonly object _lock = new();
    private readonly PriorityQueue<PendingRes, (DateTimeOffset Due, long Seq)> _pending = new();
    private readonly Queue<(DateTimeOffset At, byte[] Message)> _history = new();
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private long _startedAtTicks;
    // Force が実際の勢いを表すようになる時刻（スレの追跡を始めてから ForceWarmUp 後）。追跡前は long.MaxValue
    private long _forceReadyAtTicks = long.MaxValue;
    private long _nextNo;
    private long _pendingSeq;
    private string _status = "idle";
    private string? _statusText;
    private string? _currentTarget;
    // クライアントへ知らせる追跡中スレの一覧（{"nichan_threads":...}）。変わったときだけ配信する
    private byte[]? _threadsMessage;

    public override string Status { get { lock (_lock) return IsRunning ? _status : "idle"; } }
    public override string? StatusText { get { lock (_lock) return _statusText; } }
    public override string? CurrentTarget { get { lock (_lock) return _currentTarget; } }
    public override DateTimeOffset? VposBaseTime
    {
        get
        {
            var ticks = Interlocked.Read(ref _startedAtTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    // Force が直近 1 分の配信数として使えるか。
    // 取得を始めた直後（受信者が来た直後・休止からの再開直後）は配信数がまだ 1 分ぶんたまっておらず、
    // 0 から増えていくだけなので、その間の勢いは subject.txt から求めた値（NichanMomentumService）を使う。
    public bool IsForceReady => DateTimeOffset.UtcNow.UtcTicks >= Interlocked.Read(ref _forceReadyAtTicks);

    public NichanUpstreamChannel(string channel, string target, NichanHttpClient http,
        NichanBoardService boards, NichanBoardCatalog catalog, ProgramInfoService? programs,
        IConfiguration config, ILogger logger, MetricsService? metrics)
        : base(channel, logger, metrics, config)
    {
        _http = http;
        _options = http.Options;
        _boards = boards;
        _catalog = catalog;
        // jk104n → jk104（番組表のキー）
        _program = new NichanProgramTracker(programs, NormalizeJkThreadId(channel));
        // /watch の応答（vposBaseTime）が上流ループの開始より先に読まれても一致するよう、生成時に決める
        _startedAtTicks = DateTimeOffset.UtcNow.UtcTicks;
        if (!string.IsNullOrWhiteSpace(target))
        {
            if (NichanThreadAddress.TryParseThreadUrl(target.Trim(), out var address))
                _fixedThread = address;
            else
                _invalidTarget = target;
        }
    }

    public override Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct) =>
        Task.FromResult<ReadOnlyMemory<byte>>(
            "{\"type\":\"error\",\"data\":{\"code\":\"NOT_SUPPORTED\"}}"u8.ToArray());

    // ステータスページ用のスナップショット
    public NichanChannelStatusDto GetStatusSnapshot(int clients)
    {
        lock (_lock)
        {
            var threads = _threads.Values
                .OrderByDescending(t => t.Loader.ResCount)
                .Select(t => new NichanThreadStatusDto(
                    string.IsNullOrEmpty(t.Title) ? t.Loader.Address.Key : t.Title,
                    t.Loader.Address.ThreadUri.ToString(),
                    t.Loader.ResCount,
                    t.Finished,
                    t.Dropping,
                    t.Error,
                    t.RetryAt > DateTimeOffset.UtcNow ? t.RetryAt : null,
                    t.LastFetchAt,
                    t.LastNewResAt,
                    t.Queued,
                    t.ProgramMatch))
                .ToArray();
            return new NichanChannelStatusDto(_channel, Status, _statusText, IsRunning, clients,
                TotalComments, Force, _pending.Count, threads,
                _fixedThread == null ? _program.Title : null,
                _fixedThread == null ? _program.CurrentKeys.ToArray() : []);
        }
    }

    protected override IReadOnlyList<ReadOnlyMemory<byte>> GetInitialMessages()
    {
        lock (_lock)
        {
            TrimHistory(DateTimeOffset.UtcNow);
            var messages = new List<ReadOnlyMemory<byte>>(_history.Count + 1);
            if (_threadsMessage != null)
                messages.Add(_threadsMessage);
            messages.AddRange(_history.Select(h => (ReadOnlyMemory<byte>)h.Message));
            return messages;
        }
    }

    protected override async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            SetStatus("disabled", "5ch 取得は無効です（CacheServer:Nichan:Enabled）", null);
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        if (_invalidTarget != null)
        {
            SetStatus("error", $"スレッド URL が不正です: {_invalidTarget}", null);
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }

        _logger.LogInformation("[{Channel}] 5ch 取得を開始しました: target={Target}",
            _channel, _fixedThread?.ThreadUri.ToString() ?? "自動選択");

        Interlocked.Exchange(ref _forceReadyAtTicks, long.MaxValue);
        using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dispatchTask = DispatchLoopAsync(dispatchCts.Token);
        try
        {
            await PollLoopAsync(ct);
        }
        finally
        {
            await dispatchCts.CancelAsync();
            try { await dispatchTask; } catch (OperationCanceledException) { }
            lock (_lock)
            {
                _pending.Clear();
                _threads.Clear();
                _threadsMessage = null;
            }
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // 起動が重なったチャンネル同士で取得タイミングがそろわないよう揺らす
        await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(0, 2000)), ct);

        var nextSelect = DateTimeOffset.MinValue;
        DateTimeOffset? lastSelectAt = null;
        var lastClientSeen = DateTimeOffset.UtcNow;
        var idle = false;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;

            // /watch だけ接続されてコメント受信者がいない状態が続いたら、5ch への取得を休む。
            // 再開時は追跡状態を捨てて選び直す（休止中のレスをまとめて流さないため）。
            if (ClientCount > 0)
            {
                lastClientSeen = now;
                if (idle)
                {
                    idle = false;
                    nextSelect = DateTimeOffset.MinValue;
                    lastSelectAt = null;
                    Interlocked.Exchange(ref _forceReadyAtTicks, long.MaxValue);
                    lock (_lock) _threads.Clear();
                }
            }
            else if (now - lastClientSeen > IdleAfter)
            {
                if (!idle)
                {
                    idle = true;
                    SetStatus("idle", "コメント受信クライアントがいないため取得を休止中", null);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                continue;
            }

            // 番組が切り替わったら、次のスレ一覧更新を待たずに選び直す
            if (_fixedThread == null && _program.Refresh(now))
            {
                _logger.LogDebug("[{Channel}] 番組が変わりました: {Title} keys={Keys}",
                    _channel, _program.Title ?? "(不明)", string.Join(",", _program.CurrentKeys));
                nextSelect = now;
            }

            if (now >= nextSelect)
            {
                nextSelect = now + _options.SubjectInterval;
                try
                {
                    var selected = await SelectThreadsAsync(ct);
                    // 初回は InitialBacklog 分、それ以降に見つかったスレは前回の選択時点までさかのぼって流す
                    var backlogFrom = (lastSelectAt ?? now) - _options.InitialBacklog;
                    ApplySelection(selected, backlogFrom);
                    if (lastSelectAt == null)
                        Interlocked.Exchange(ref _forceReadyAtTicks, (DateTimeOffset.UtcNow + ForceWarmUp).UtcTicks);
                    lastSelectAt = now;
                    // さかのぼって流すレスより先にスレ一覧が届くよう、選択直後にも送る
                    await PublishThreadListAsync();
                }
                catch (NichanHostBlockedException ex)
                {
                    nextSelect = ex.Until;
                    SetStatus("blocked", ex.Message, CurrentTarget);
                }
                catch (NichanException ex)
                {
                    _logger.LogWarning("[{Channel}] 5ch スレ一覧の取得に失敗しました: {Message}", _channel, ex.Message);
                    SetStatus("error", ex.Message, CurrentTarget);
                }
            }

            ThreadState[] targets;
            lock (_lock)
                targets = _threads.Values.Where(t => !t.Finished).ToArray();
            foreach (var state in targets)
            {
                if (ct.IsCancellationRequested) break;
                if (DateTimeOffset.UtcNow < state.RetryAt) continue;
                await UpdateThreadAsync(state, ct);
            }
            lock (_lock)
            {
                foreach (var (key, _) in _threads.Where(kv => kv.Value.Dropping).ToArray())
                    _threads.Remove(key);
            }
            UpdateRunningStatus();
            await PublishThreadListAsync();

            await Task.Delay(_options.DatInterval, ct);
        }
    }

    private async Task<IReadOnlyList<NichanSelectedThread>> SelectThreadsAsync(CancellationToken ct)
    {
        if (_fixedThread != null)
            return [new NichanSelectedThread(new NichanThreadInfo(_fixedThread, "", 0), false)];

        var entries = _catalog.Get(_channel);
        if (entries.Count == 0)
            throw new NichanException("このチャンネルに対応する 5ch の板が設定されていません（CacheServer:Nichan:Boards）");

        var (programKeys, programOnlyKeys) = _program.GetKeys(DateTimeOffset.UtcNow);
        return await NichanThreadSelector.SelectAsync(_boards, entries, programKeys, programOnlyKeys,
            _options.MaxThreadsPerChannel, ct);
    }

    private void ApplySelection(IReadOnlyList<NichanSelectedThread> selectedThreads, DateTimeOffset backlogFrom)
    {
        lock (_lock)
        {
            var selected = selectedThreads.Select(s => s.Thread).ToArray();
            var programMatches = selectedThreads.Where(s => s.ProgramMatch)
                .Select(s => s.Thread.Address.Identity).ToHashSet(StringComparer.Ordinal);
            var keep = selected.Select(t => t.Address.Identity).ToHashSet(StringComparer.Ordinal);
            // 選択から外れたスレは、取りこぼしを防ぐため次の取得を 1 回済ませてから外す
            foreach (var (key, old) in _threads.Where(kv => !keep.Contains(kv.Key)).ToArray())
            {
                if (old.Finished)
                    _threads.Remove(key);
                else
                    old.Dropping = true;
            }
            foreach (var thread in selected)
            {
                if (_threads.TryGetValue(thread.Address.Identity, out var state))
                {
                    state.Dropping = false;
                    state.ProgramMatch = programMatches.Contains(thread.Address.Identity);
                    if (!string.IsNullOrEmpty(thread.Title))
                        state.Title = thread.Title;
                    continue;
                }
                _threads[thread.Address.Identity] = new ThreadState(new NichanDatLoader(thread.Address))
                {
                    Title = thread.Title,
                    BacklogFrom = backlogFrom,
                    ProgramMatch = programMatches.Contains(thread.Address.Identity),
                };
            }
            if (_threads.Values.All(t => t.Dropping))
            {
                _status = "noThread";
                _statusText = "対応するスレがありません";
                _currentTarget = null;
            }
        }
    }

    private async Task UpdateThreadAsync(ThreadState state, CancellationToken ct)
    {
        IReadOnlyList<NichanRes> newRes;
        try
        {
            newRes = await state.Loader.UpdateAsync(_http, ct);
        }
        catch (NichanThreadGoneException ex)
        {
            lock (_lock)
            {
                state.Finished = true;
                state.Error = ex.Message;
            }
            _logger.LogInformation("[{Channel}] 5ch スレの取得を終了します: {Uri} {Message}",
                _channel, state.Loader.Address.ThreadUri, ex.Message);
            return;
        }
        catch (NichanHostBlockedException ex)
        {
            lock (_lock)
            {
                state.RetryAt = ex.Until;
                state.Error = ex.Message;
            }
            return;
        }
        catch (NichanException ex)
        {
            lock (_lock)
            {
                state.Failures++;
                state.RetryAt = DateTimeOffset.UtcNow +
                    TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(2, Math.Min(state.Failures, 6))));
                state.Error = ex.Message;
            }
            _logger.LogWarning("[{Channel}] 5ch dat の取得に失敗しました: {Uri} {Message}",
                _channel, state.Loader.Address.ThreadUri, ex.Message);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var added = 0;
        lock (_lock)
        {
            state.LastFetchAt = now;
            state.Failures = 0;
            state.Error = null;
            if (state.Loader.Title != null)
                state.Title = state.Loader.Title;
            if (state.Loader.ResCount >= 1000)
                state.Finished = true;

            foreach (var res in newRes)
            {
                if (res.Number <= 1 || res.Number > 1000 || res.PostedAt is not { } postedAt)
                    continue;
                if (postedAt < state.BacklogFrom)
                    continue;
                var text = NichanDatParser.BodyToComment(res.BodyHtml, _options.MaxLines, _options.MaxCommentLength);
                if (text == null)
                    continue;
                var userId = res.Id != null
                    ? "5ch:" + res.Id
                    : $"5ch:{state.Loader.Address.Key}-{res.Number}";
                // 書き込み時刻が未来（時計ずれ）でも、配信は最大 PlaybackDelay 後までに抑える
                var due = postedAt + _options.PlaybackDelay;
                if (due > now + _options.PlaybackDelay)
                    due = now + _options.PlaybackDelay;
                _pending.Enqueue(new PendingRes(postedAt, userId, text, state.Loader.Address.Identity, res.Number, due),
                    (due, ++_pendingSeq));
                added++;
            }
        }
        if (added > 0)
        {
            lock (_lock)
            {
                state.LastNewResAt = now;
                state.Queued += added;
            }
        }
        if (added > 0)
            _logger.LogDebug("[{Channel}] 5ch 新着レス: {Uri} count={Count}",
                _channel, state.Loader.Address.ThreadUri, added);
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            List<byte[]>? messages = null;
            lock (_lock)
            {
                while (_pending.TryPeek(out var item, out var priority) && priority.Due <= now)
                {
                    _pending.Dequeue();
                    var no = Interlocked.Increment(ref _nextNo);
                    var message = CreateChatJson(no, item, now);
                    _history.Enqueue((now, message));
                    // さかのぼって流すレス（配信予定時刻が過去）は、その予定時刻で勢いに数える
                    RecordComment(no, item.Due);
                    (messages ??= []).Add(message);
                }
                TrimHistory(now);
            }
            if (messages == null) continue;
            foreach (var message in messages)
                await BroadcastAsync(message);
        }
    }

    private byte[] CreateChatJson(long no, PendingRes res, DateTimeOffset now)
    {
        var baseTicks = Interlocked.Read(ref _startedAtTicks);
        var vpos = baseTicks == 0 ? 0
            : Math.Max(0, (int)((now.UtcTicks - baseTicks) / TimeSpan.TicksPerMillisecond / 10));
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("chat");
            writer.WriteStartObject();
            writer.WriteString("thread", GetDownstreamThreadId(_channel));
            writer.WriteNumber("no", no);
            writer.WriteNumber("vpos", vpos);
            writer.WriteNumber("date", res.PostedAt.ToUnixTimeSeconds());
            writer.WriteNumber("date_usec", res.PostedAt.ToUnixTimeMilliseconds() % 1000 * 1000);
            writer.WriteString("user_id", res.UserId);
            writer.WriteNumber("premium", 0);
            writer.WriteNumber("anonymity", 1);
            writer.WriteString("content", res.Text);
            // どのスレのレスか（クライアントのスレ絞り込み用。避難所互換クライアントは無視する）
            writer.WriteString("nichan_thread", res.ThreadId);
            writer.WriteNumber("nichan_res", res.ResNo);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return ms.ToArray();
    }

    // 追跡中のスレ一覧を {"nichan_threads":{...}} として、内容が変わったときだけ全クライアントへ送る。
    // レス数は頻繁に変わるので含めない（スレの入れ替え・スレタイ・番組一致・終了の変化だけで送る）。
    private async Task PublishThreadListAsync()
    {
        byte[] message;
        lock (_lock)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("nichan_threads");
                writer.WriteStartObject();
                writer.WriteString("thread", GetDownstreamThreadId(_channel));
                if (_fixedThread == null && _program.Title is { } programTitle)
                    writer.WriteString("program", programTitle);
                else
                    writer.WriteNull("program");
                writer.WritePropertyName("threads");
                writer.WriteStartArray();
                foreach (var t in _threads.Values
                             .Where(t => !t.Dropping)
                             .OrderByDescending(t => t.ProgramMatch)
                             .ThenByDescending(t => t.Loader.Address.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", t.Loader.Address.Identity);
                    writer.WriteString("title", string.IsNullOrEmpty(t.Title) ? t.Loader.Address.Key : t.Title);
                    writer.WriteString("url", t.Loader.Address.ThreadUri.ToString());
                    writer.WriteBoolean("programMatch", t.ProgramMatch);
                    writer.WriteBoolean("finished", t.Finished);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            message = ms.ToArray();
            if (_threadsMessage != null && _threadsMessage.AsSpan().SequenceEqual(message))
                return;
            _threadsMessage = message;
        }
        await BroadcastAsync(message);
    }

    private void TrimHistory(DateTimeOffset now)
    {
        while (_history.Count > 0 &&
               (_history.Count > _options.HistoryLimit || now - _history.Peek().At > _options.HistoryDuration))
            _history.Dequeue();
    }

    private void UpdateRunningStatus()
    {
        lock (_lock)
        {
            if (_threads.Count == 0)
                return;
            var active = _threads.Values.OrderByDescending(t => t.Loader.ResCount).ToArray();
            var blocked = active.Where(t => t.RetryAt > DateTimeOffset.UtcNow && t.Error != null).ToArray();
            _status = blocked.Length == active.Length ? "error" : "running";
            _statusText = string.Join(" / ", active.Select(t =>
                $"{(string.IsNullOrEmpty(t.Title) ? t.Loader.Address.Key : t.Title)} ({t.Loader.ResCount})" +
                (t.Finished ? " [終了]" : t.Error != null ? $" [{t.Error}]" : "")));
            _currentTarget = active.FirstOrDefault(t => !t.Finished)?.Loader.Address.ThreadUri.ToString()
                ?? active[0].Loader.Address.ThreadUri.ToString();
        }
    }

    private void SetStatus(string status, string? text, string? target)
    {
        lock (_lock)
        {
            _status = status;
            _statusText = text;
            _currentTarget = target;
        }
    }

    private sealed record PendingRes(DateTimeOffset PostedAt, string UserId, string Text, string ThreadId, int ResNo,
        DateTimeOffset Due);

    private sealed class ThreadState
    {
        public ThreadState(NichanDatLoader loader) => Loader = loader;
        public NichanDatLoader Loader { get; }
        public string? Title { get; set; }
        public DateTimeOffset BacklogFrom { get; set; }
        public DateTimeOffset RetryAt { get; set; } = DateTimeOffset.MinValue;
        public int Failures { get; set; }
        public bool Finished { get; set; }
        public bool Dropping { get; set; }
        public DateTimeOffset? LastFetchAt { get; set; }
        public DateTimeOffset? LastNewResAt { get; set; }
        public long Queued { get; set; }
        public bool ProgramMatch { get; set; }
        public string? Error { get; set; }
    }
}
