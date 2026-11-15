using System.Globalization;
using System.Text;
using System.Text.Json;

namespace jkcnsl_cache.Nichan;

// 5ch 実況スレを上流とするチャンネル（appsettings の値が "5ch:" / "5ch:{read.cgi URL}"）。
// subject.txt からスレを選び、dat を差分取得し、レスを避難所互換の chat JSON にして配信する。
// レスは「書き込み時刻 + PlaybackDelay」に配信し、ポーリングでまとめて届いたレスを書き込み時の間隔で流す。
public sealed class NichanUpstreamChannel : UpstreamChannelBase
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    // 番組が切り替わった後も、前の番組のスレをこの時間は番組一致として扱う
    private static readonly TimeSpan PreviousProgramGrace = TimeSpan.FromMinutes(5);
    private const string AnimeGenreCode = "0x7";

    private readonly NichanOptions _options;
    private readonly NichanHttpClient _http;
    private readonly NichanBoardService _boards;
    private readonly NichanBoardCatalog _catalog;
    private readonly ProgramInfoService? _programs;
    private readonly string _programVideo;
    private readonly NichanThreadAddress? _fixedThread;
    private readonly string? _invalidTarget;
    private readonly object _lock = new();
    private readonly PriorityQueue<PendingRes, (DateTimeOffset Due, long Seq)> _pending = new();
    private readonly Queue<(DateTimeOffset At, byte[] Message)> _history = new();
    private readonly Dictionary<string, ThreadState> _threads = new(StringComparer.Ordinal);
    private long _startedAtTicks;
    private long _nextNo;
    private long _pendingSeq;
    private string _status = "idle";
    private string? _statusText;
    private string? _currentTarget;
    // 番組表による現在の番組（スレ選択で番組名に一致するスレを優先する）
    private string? _programTitle;
    private ProgramKeys _currentProgram = ProgramKeys.Empty;
    private ProgramKeys _previousProgram = ProgramKeys.Empty;
    private DateTimeOffset _previousProgramUntil = DateTimeOffset.MinValue;

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

    public NichanUpstreamChannel(string channel, string target, NichanHttpClient http,
        NichanBoardService boards, NichanBoardCatalog catalog, ProgramInfoService? programs,
        IConfiguration config, ILogger logger, MetricsService? metrics)
        : base(channel, logger, metrics, config)
    {
        _http = http;
        _options = http.Options;
        _boards = boards;
        _catalog = catalog;
        _programs = programs;
        // jk104n → jk104（番組表のキー）
        _programVideo = NormalizeJkThreadId(channel);
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
                _fixedThread == null ? _programTitle : null,
                _fixedThread == null ? _currentProgram.Keys.ToArray() : []);
        }
    }

    protected override IReadOnlyList<ReadOnlyMemory<byte>> GetInitialMessages()
    {
        lock (_lock)
        {
            TrimHistory(DateTimeOffset.UtcNow);
            return _history.Select(h => (ReadOnlyMemory<byte>)h.Message).ToArray();
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
            if (_fixedThread == null && RefreshProgram(now))
                nextSelect = now;

            if (now >= nextSelect)
            {
                nextSelect = now + _options.SubjectInterval;
                try
                {
                    var selected = await SelectThreadsAsync(ct);
                    // 初回は InitialBacklog 分、それ以降に見つかったスレは前回の選択時点までさかのぼって流す
                    var backlogFrom = (lastSelectAt ?? now) - _options.InitialBacklog;
                    ApplySelection(selected, backlogFrom);
                    lastSelectAt = now;
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

            await Task.Delay(_options.DatInterval, ct);
        }
    }

    private async Task<IReadOnlyList<SelectedThread>> SelectThreadsAsync(CancellationToken ct)
    {
        if (_fixedThread != null)
            return [new SelectedThread(new NichanThreadInfo(_fixedThread, "", 0), false)];

        var entries = _catalog.Get(_channel);
        if (entries.Count == 0)
            throw new NichanException("このチャンネルに対応する 5ch の板が設定されていません（CacheServer:Nichan:Boards）");

        var now = DateTimeOffset.UtcNow;
        ProgramKeys current, previous;
        lock (_lock)
        {
            current = _currentProgram;
            previous = now < _previousProgramUntil ? _previousProgram : ProgramKeys.Empty;
        }
        var programKeys = current.Keys.Concat(previous.Keys).Distinct().ToArray();
        // アニメ実況板のような番組専用の板は、番組がアニメのとき（ジャンル不明を含む）だけ見る
        var programOnlyKeys = current.Keys.Where(_ => current.AllowsProgramOnly)
            .Concat(previous.Keys.Where(_ => previous.AllowsProgramOnly))
            .Distinct()
            .ToArray();

        var candidates = new List<(NichanThreadInfo Thread, double Momentum, bool ProgramMatch)>();
        NichanException? lastError = null;
        var succeeded = 0;
        foreach (var entry in entries)
        {
            if (entry.ProgramOnly && programOnlyKeys.Length == 0)
                continue;
            IReadOnlyList<NichanThreadInfo> threads;
            try
            {
                threads = await _boards.GetThreadsAsync(entry.Board, ct);
                succeeded++;
            }
            catch (NichanException ex)
            {
                lastError = ex;
                continue;
            }
            var keywords = entry.Keywords.Select(NormalizeTitle).ToArray();
            var excludes = entry.Excludes.Select(NormalizeTitle).ToArray();
            foreach (var thread in threads)
            {
                if (thread.ResCount >= 1000) continue;
                var title = NormalizeTitle(thread.Title);
                if (keywords.Length > 0 && !keywords.Any(title.Contains)) continue;
                if (excludes.Any(title.Contains)) continue;
                var compact = NichanProgramMatcher.Compact(thread.Title);
                var programMatch = entry.ProgramOnly
                    ? NichanProgramMatcher.Matches(compact, programOnlyKeys)
                    : programKeys.Length > 0 && NichanProgramMatcher.Matches(compact, programKeys);
                if (entry.ProgramOnly && !programMatch) continue;
                // 勢い（1 時間あたりのレス数）。立ったばかりのスレが過大にならないよう経過時間に下限を置く
                var ageHours = thread.Address.CreatedAt is { } created
                    ? Math.Max(0.1, (now - created).TotalHours)
                    : 24;
                candidates.Add((thread, thread.ResCount / ageHours, programMatch));
            }
        }
        if (succeeded == 0 && lastError != null)
            throw lastError;

        var ordered = candidates
            .GroupBy(c => c.Thread.Address.Identity)
            .Select(g => g.OrderByDescending(c => c.ProgramMatch).First())
            .OrderByDescending(c => c.Momentum)
            .ToList();
        // 番組名に一致するスレのうち最も勢いのあるものを必ず含め、残りは勢い順で埋める。
        // 前の番組のスレがまだ伸びていても、始まったばかりの番組のスレを取りこぼさないため。
        var result = new List<SelectedThread>();
        var bestMatch = ordered.FirstOrDefault(c => c.ProgramMatch);
        if (bestMatch.Thread != null)
            result.Add(new SelectedThread(bestMatch.Thread, true));
        foreach (var c in ordered)
        {
            if (result.Count >= _options.MaxThreadsPerChannel) break;
            if (bestMatch.Thread != null && c.Thread.Address.Identity == bestMatch.Thread.Address.Identity) continue;
            result.Add(new SelectedThread(c.Thread, c.ProgramMatch));
        }
        return result;
    }

    // 番組表から現在の番組を読み、変わっていれば true を返す
    private bool RefreshProgram(DateTimeOffset now)
    {
        var program = _programs?.GetProgram(_programVideo);
        var title = program != null && program.StartAt <= now && now < program.EndAt
            ? program.Title
            : null;
        lock (_lock)
        {
            if (title == _programTitle)
                return false;
            if (_programTitle != null)
            {
                _previousProgram = _currentProgram;
                _previousProgramUntil = now + PreviousProgramGrace;
            }
            _programTitle = title;
            _currentProgram = new ProgramKeys(
                NichanProgramMatcher.BuildKeys(title),
                program?.GenreCode is null || program.GenreCode == AnimeGenreCode);
        }
        _logger.LogDebug("[{Channel}] 番組が変わりました: {Title} keys={Keys}",
            _channel, title ?? "(不明)", string.Join(",", _currentProgram.Keys));
        return true;
    }

    private void ApplySelection(IReadOnlyList<SelectedThread> selectedThreads, DateTimeOffset backlogFrom)
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
                _pending.Enqueue(new PendingRes(postedAt, userId, text), (due, ++_pendingSeq));
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
                    RecordComment(no);
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
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return ms.ToArray();
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

    // NFKC 正規化 + 小文字化（"ＢＳ朝日" と "BS朝日" を同一視する）。
    // 空白は残す（"BS10 1150" が "BS11" に一致しないように）
    private static string NormalizeTitle(string title) =>
        title.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);

    private sealed record SelectedThread(NichanThreadInfo Thread, bool ProgramMatch);

    // 番組名から作った照合キーと、番組専用の板（アニメ実況板など）を見てよいか
    private sealed record ProgramKeys(IReadOnlyList<string> Keys, bool AllowsProgramOnly)
    {
        public static readonly ProgramKeys Empty = new([], false);
    }

    private sealed record PendingRes(DateTimeOffset PostedAt, string UserId, string Text);

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
