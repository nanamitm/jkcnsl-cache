using dwango.nicolive.chat.data;
using dwango.nicolive.chat.service.edge;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace jkcnsl_cache;

public sealed class NicovideoUpstreamChannel : UpstreamChannelBase
{
    private readonly string _lvId;
    private readonly TimeSpan _noStreamCheckInterval;
    private readonly int _fallbackMaxCommentLength;
    private readonly NicovideoSearchService? _searchService;
    private volatile string? _currentLvId;
    private volatile string? _scheduledLvId;
    private volatile bool _isScheduled;
    private DateTimeOffset? _scheduledStartUtc;
    private volatile string? _failedNonOfficialLvId;
    private volatile bool _isRetryWaiting;
    private long _upstreamVposBaseTicks; // 0 = 未取得、正値 = UTC Ticks
    private static readonly TimeSpan NonOfficialCandidateRetryInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NonOfficialCandidateRetryLimit = TimeSpan.FromMinutes(1);

    public override string? CurrentTarget => _currentLvId ?? _scheduledLvId ?? (!string.IsNullOrEmpty(_lvId) ? _lvId : null);
    // 接続中の lv??? または ch??? を返す（per-client 投稿セッションのスクレイピングに使用）
    public override string? WatchTarget =>
        !string.IsNullOrEmpty(_currentLvId) ? _currentLvId :
        !string.IsNullOrEmpty(_lvId) ? _lvId : null;
    public override bool IsScheduled => _isScheduled;
    public override DateTimeOffset? ScheduledStartUtc => _scheduledStartUtc;
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
    public override string Status => IsLocalFallbackActive ? "fallbackLocal" : _isRetryWaiting ? "retryWaiting" : base.Status;
    public override string? StatusText => IsLocalFallbackActive ? "ローカル待避中" : _isRetryWaiting ? "配信情報取得失敗" : null;

    // 匿名接続のため投稿不可（per-client 認証は未実装）
    public override Task<ReadOnlyMemory<byte>> PostCommentAsync(ReadOnlyMemory<byte> json, CancellationToken ct)
    {
        if (IsLocalFallbackActive)
            return PostLocalFallbackCommentAsync(json, null, _fallbackMaxCommentLength, ct);

        _logger.LogWarning("[{Channel}] コメント投稿は匿名接続では対応していません（要認証）", _channel);
        return Task.FromResult<ReadOnlyMemory<byte>>(
            "{\"type\":\"error\",\"data\":{\"code\":\"COMMENT_POST_NOT_ALLOWED\"}}"u8.ToArray());
    }

    private readonly HttpClient _pageClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _streamClient = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
        UseCookies = false
    });

    private const int MaxChunkSize = 1048576;

    public NicovideoUpstreamChannel(string channel, string lvId, IConfiguration config, ILogger logger, TimeSpan noStreamCheckInterval,
        MetricsService metrics, NicovideoSearchService? searchService = null)
        : base(channel, logger, metrics)
    {
        _lvId = lvId;
        _noStreamCheckInterval = noStreamCheckInterval;
        _searchService = searchService;
        _fallbackMaxCommentLength = Math.Max(1, config.GetValue<int>("CacheServer:LocalStream:MaxCommentLength", 75));
        _pageClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _pageClient.DefaultRequestHeaders.Add("Accept", "*/*");
        _streamClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _streamClient.DefaultRequestHeaders.Add("Accept", "*/*");
        _streamClient.DefaultRequestHeaders.Add("Origin", "https://live.nicovideo.jp");
        _streamClient.DefaultRequestHeaders.Add("Referer", "https://live.nicovideo.jp/");
    }

    protected override async Task ConnectAndReceiveAsync(CancellationToken ct)
    {
        // 1. 公式ch で WebSocket URL を取得
        var activeLvId = _lvId;
        var webSocketUrl = !string.IsNullOrEmpty(_lvId)
            ? await ScrapeWatchPageAsync(_lvId, ct)
            : null;

        // 2. 非公式チャンネル: 検索サービスから次のバッチ結果を受け取る
        if (webSocketUrl == null && _searchService != null)
        {
            SetLocalFallbackActive(true);
            var result = await _searchService.WaitForNextResultAsync(_channel, _failedNonOfficialLvId, ct);
            if (result == null)
            {
                _logger.LogWarning("[{Channel}] 配信が見つかりません", _channel);
                return;
            }
            if (result.IsReserved)
            {
                _scheduledLvId = result.LvId;
                _scheduledStartUtc = result.ScheduledStartUtc.HasValue
                    ? (DateTimeOffset?)result.ScheduledStartUtc.Value : null;
                _isScheduled = true;
                _isRetryWaiting = false;
                _logger.LogDebug("[{Channel}] 配信予定 {Time} JST - 次の検索まで待機",
                    _channel, result.ScheduledStartUtc.HasValue
                        ? result.ScheduledStartUtc.Value.AddHours(9).ToString("MM/dd HH:mm") : "時刻不明");
                return;
            }
            _scheduledLvId = null;
            _scheduledStartUtc = null;
            _isScheduled = false;
            _isRetryWaiting = false;
            _logger.LogInformation("[{Channel}] 非公式配信にフォールバック: {LvId}", _channel, result.LvId);
            webSocketUrl = await ScrapeWatchPageAsync(result.LvId, ct);
            if (webSocketUrl != null)
            {
                activeLvId = result.LvId;
                _failedNonOfficialLvId = null;
            }
            else
            {
                _failedNonOfficialLvId = result.LvId;
                _logger.LogWarning("[{Channel}] 非公式配信のスクレイピング失敗。短時間再検索します: {LvId}", _channel, result.LvId);
                var retryDeadline = DateTimeOffset.UtcNow + NonOfficialCandidateRetryLimit;

                while (DateTimeOffset.UtcNow < retryDeadline && !ct.IsCancellationRequested)
                {
                    await Task.Delay(NonOfficialCandidateRetryInterval, ct);
                    result = await _searchService.WaitForNextResultAsync(_channel, _failedNonOfficialLvId, ct);
                    if (result == null)
                    {
                        _logger.LogDebug("[{Channel}] 非公式配信の短時間再検索で候補が見つかりません", _channel);
                        continue;
                    }
                    if (result.IsReserved)
                    {
                        _scheduledLvId = result.LvId;
                        _scheduledStartUtc = result.ScheduledStartUtc.HasValue
                            ? (DateTimeOffset?)result.ScheduledStartUtc.Value : null;
                        _isScheduled = true;
                        _isRetryWaiting = false;
                        _logger.LogDebug("[{Channel}] 非公式配信の短時間再検索で配信予定を検出: {LvId}", _channel, result.LvId);
                        return;
                    }

                    _logger.LogInformation("[{Channel}] 非公式配信の次候補にフォールバック: {LvId}", _channel, result.LvId);
                    webSocketUrl = await ScrapeWatchPageAsync(result.LvId, ct);
                    if (webSocketUrl != null)
                    {
                        activeLvId = result.LvId;
                        _failedNonOfficialLvId = null;
                        _scheduledLvId = null;
                        _scheduledStartUtc = null;
                        _isScheduled = false;
                        _isRetryWaiting = false;
                        break;
                    }

                    _failedNonOfficialLvId = result.LvId;
                    _logger.LogWarning("[{Channel}] 非公式配信の次候補もスクレイピング失敗: {LvId}", _channel, result.LvId);
                }

                if (webSocketUrl == null)
                {
                    _scheduledLvId = _failedNonOfficialLvId;
                    _scheduledStartUtc = null;
                    _isScheduled = false;
                    _isRetryWaiting = true;
                    SetLocalFallbackActive(true);
                    _logger.LogWarning("[{Channel}] 非公式配信の短時間再検索に失敗。次のバッチまで待機します", _channel);
                    return;
                }
            }
        }

        // 3. 配信なし（公式チャンネルのみ到達）
        //    メンテナンス等で watch ページ取得に失敗した場合、通常バックオフだと最大5分で再試行する。
        //    外部サイトへの過剰アクセスを避けるため、配信なし確認と同じ間隔まで待機する。
        if (webSocketUrl == null)
        {
            _isRetryWaiting = true;
            SetLocalFallbackActive(true);
            _logger.LogWarning("[{Channel}] 配信情報を取得できません。{Minutes}分後に再確認します",
                _channel, Math.Round(_noStreamCheckInterval.TotalMinutes, 1));
            await Task.Delay(_noStreamCheckInterval, ct);
            return;
        }

        // 視聴セッション WebSocket 接続
        using var watchWs = new ClientWebSocket();
        watchWs.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");
        watchWs.Options.SetRequestHeader("Origin", "https://live.nicovideo.jp");
        watchWs.Options.SetRequestHeader("Cache-Control", "no-cache");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Dest", "empty");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Mode", "websocket");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Site", "same-site");

        _currentLvId = activeLvId;
        _scheduledLvId = null;
        _scheduledStartUtc = null;
        _isScheduled = false;
        _isRetryWaiting = false;
        SetLocalFallbackActive(false);
        _logger.LogInformation("[{Channel}] nicovideo WebSocketへ接続: {Url}", _channel, webSocketUrl);
        await watchWs.ConnectAsync(new Uri(webSocketUrl), ct);
        await SendTextAsync(watchWs, """{"type":"startWatching","data":{"reconnect":false}}""", ct);

        var state = new NicovideoState { ActiveLvId = activeLvId };
        var watchBuf = new byte[32768];
        int watchCount = 0;

        Task<WebSocketReceiveResult>? watchRecvTask = null;
        Task? entryTask = null;
        Task? segmentTask = null;
        Task? prefetchTask = null;
        Stream? entryStream = null;
        Stream? segmentStream = null;
        Stream? prefetchStream = null;
        var msEntry = new MemoryStream();
        var msSegment = new MemoryStream();
        var msPrefetch = new MemoryStream();
        var readBuf = new byte[512];
        Task keepSeatTask = Task.Delay(Timeout.Infinite, ct);


        try
        {
            while (!ct.IsCancellationRequested && watchWs.State == WebSocketState.Open)
            {
                // keepSeat
                if (state.KeepSeatIntervalSec > 0)
                    keepSeatTask = Task.Delay(state.KeepSeatIntervalSec * 1000, ct);

                // HTTP entryストリーム開始
                if (entryTask == null && state.ViewUri != null)
                {
                    var entryUrl = $"{state.ViewUri}?at={state.NextAt}";
                    state.NextAt = null;
                    entryTask = FetchEntryStreamAsync(entryUrl, ct);
                }

                watchRecvTask ??= watchWs.ReceiveAsync(
                    new ArraySegment<byte>(watchBuf, watchCount, watchBuf.Length - watchCount), ct);

                var tasks = new List<Task> { watchRecvTask, keepSeatTask };
                if (entryTask != null) tasks.Add(entryTask);
                if (segmentTask != null) tasks.Add(segmentTask);
                if (prefetchTask != null) tasks.Add(prefetchTask);

                var completed = await Task.WhenAny(tasks);

                if (completed == keepSeatTask)
                {
                    await SendTextAsync(watchWs, """{"type":"keepSeat"}""", ct);
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
                        await HandleWatchMessageAsync(watchWs, watchBuf, watchCount, state, ct);
                        watchCount = 0;
                    }
                    continue;
                }

                if (completed == entryTask)
                {
                    if (entryStream == null)
                    {
                        entryStream = await (Task<Stream>)entryTask;
                        entryTask = ReadProtoBufChunkAsync(entryStream, msEntry, readBuf, ct);
                    }
                    else
                    {
                        var ms = await (Task<MemoryStream?>)entryTask;
                        entryTask = null;
                        if (ms == null)
                        {
                            entryStream.Close();
                            entryStream = null;
                        }
                        else
                        {
                            var chunkedEntry = ProtoBuf.Serializer.Deserialize<ChunkedEntry>(ms);
                            if (chunkedEntry.next != null)
                                state.NextAt = chunkedEntry.next.at.ToString();
                            _logger.LogDebug("[{Channel}] ChunkedEntry: next={Next} segment={Seg}",
                                _channel, chunkedEntry.next?.at, chunkedEntry.segment?.uri ?? "(null)");
                            if (chunkedEntry.segment != null)
                            {
                                var segUri = chunkedEntry.segment.uri;
                                if (segmentTask == null)
                                    segmentTask = _streamClient.GetStreamAsync(segUri, ct);
                                else if (prefetchTask == null)
                                {
                                    prefetchStream?.Close();
                                    prefetchStream = null;
                                    prefetchTask = _streamClient.GetStreamAsync(segUri, ct);
                                }
                            }
                            entryTask = ReadProtoBufChunkAsync(entryStream, msEntry, readBuf, ct);
                        }
                    }
                    continue;
                }

                ChunkedMessage? chunkedMessage = null;

                if (completed == segmentTask)
                {
                    if (segmentStream == null)
                    {
                        segmentStream = await (Task<Stream>)segmentTask;
                        segmentTask = ReadProtoBufChunkAsync(segmentStream, msSegment, readBuf, ct);
                    }
                    else
                    {
                        var ms = await (Task<MemoryStream?>)segmentTask;
                        segmentTask = null;
                        if (ms == null)
                        {
                            segmentStream.Close();
                            // プリフェッチを引き継ぐ
                            segmentTask = prefetchTask;
                            segmentStream = prefetchStream;
                            prefetchTask = null;
                            prefetchStream = null;
                            if (segmentTask == null && segmentStream != null)
                            {
                                chunkedMessage = ProtoBuf.Serializer.Deserialize<ChunkedMessage>(msPrefetch);
                                segmentTask = ReadProtoBufChunkAsync(segmentStream, msSegment, readBuf, ct);
                            }
                        }
                        else
                        {
                            chunkedMessage = ProtoBuf.Serializer.Deserialize<ChunkedMessage>(ms);
                            segmentTask = ReadProtoBufChunkAsync(segmentStream, msSegment, readBuf, ct);
                        }
                    }
                }
                else if (completed == prefetchTask)
                {
                    if (prefetchStream == null)
                    {
                        prefetchStream = await (Task<Stream>)prefetchTask;
                        prefetchTask = ReadProtoBufChunkAsync(prefetchStream, msPrefetch, readBuf, ct);
                    }
                    else if (await (Task<MemoryStream?>)prefetchTask == null)
                    {
                        prefetchTask = null;
                        prefetchStream.Close();
                        prefetchStream = null;
                    }
                    else
                    {
                        prefetchTask = null;
                    }
                }

                if (chunkedMessage?.message?.chat != null &&
                    chunkedMessage.meta?.at != null &&
                    state.ServerUnixTime > TimeSpan.Zero)
                {
                    var at = chunkedMessage.meta.at.Value.ToUniversalTime() - DateTime.UnixEpoch;
                    var json = BuildChatJson(state.ThreadId, chunkedMessage.message.chat, at);
                    RecordComment((long)chunkedMessage.message.chat.no);
                    await BroadcastAsync(json);
                }
            }
        }
        catch (EntryStream400Exception)
        {
            // entry ストリーム 400 = 配信未開始/終了/枠切替直後など。
            // 公式チャンネルの午前4時リセットでも起こるため、30分固定待機ではなく外側の通常バックオフに任せる。
            _logger.LogWarning("[{Channel}] entry ストリームが一時的に利用できません。通常バックオフで再接続します", _channel);
            await watchWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            throw;
        }
        finally
        {
            _currentLvId = null;
            _scheduledLvId = null;
            _scheduledStartUtc = null;
            _isScheduled = false;
            if (!_isRetryWaiting) SetLocalFallbackActive(true);
            if (!_isRetryWaiting) _isRetryWaiting = false;
            prefetchStream?.Close();
            segmentStream?.Close();
            entryStream?.Close();
        }
    }

    private async Task<string?> ScrapeWatchPageAsync(string lvId, CancellationToken ct)
    {
        try
        {
            var watchUrl = $"https://live.nicovideo.jp/watch/{lvId}";
            using var response = await _pageClient.GetAsync(watchUrl, ct);
            var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? watchUrl;
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} http={Status} finalUrl={Url}",
                    _channel, lvId, (int)response.StatusCode, finalUrl);
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            var match = Regex.Match(html, @"<script[^>]* id=""embedded-data""[^>]*>");
            if (!match.Success)
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=embedded-dataなし finalUrl={Url} bytes={Bytes}",
                    _channel, lvId, finalUrl, Encoding.UTF8.GetByteCount(html));
                return null;
            }
            match = Regex.Match(match.Value, @" data-props=""([^""]*)""");
            if (!match.Success)
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=data-propsなし finalUrl={Url}",
                    _channel, lvId, finalUrl);
                return null;
            }

            using var doc = JsonDocument.Parse(HttpUtility.HtmlDecode(match.Groups[1].Value));
            if (!doc.RootElement.TryGetProperty("site", out var site))
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=siteなし finalUrl={Url}",
                    _channel, lvId, finalUrl);
                return null;
            }
            if (!site.TryGetProperty("relive", out var relive))
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=reliveなし finalUrl={Url}",
                    _channel, lvId, finalUrl);
                return null;
            }
            if (!relive.TryGetProperty("webSocketUrl", out var wsUrl))
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=webSocketUrlなし finalUrl={Url}",
                    _channel, lvId, finalUrl);
                return null;
            }
            var url = wsUrl.GetString();
            if (!Regex.IsMatch(url ?? "", @"^wss://[0-9A-Za-z.-]+\.nicovideo\.jp/"))
            {
                _logger.LogWarning("[{Channel}] ページスクレイピング失敗: target={Target} reason=不正なwebSocketUrl url={Url}",
                    _channel, lvId, url ?? "(null)");
                return null;
            }
            return url;
        }
        catch (JsonException ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[{Channel}] ページスクレイピング失敗: target={Target} reason=embedded-data JSON解析失敗",
                _channel, lvId);
            return null;
        }
        catch (HttpRequestException ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[{Channel}] ページスクレイピング失敗: target={Target} reason=HTTP要求失敗 status={Status}",
                _channel, lvId, ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null);
            return null;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[{Channel}] ページスクレイピング失敗: target={Target} reason=例外", _channel, lvId);
            return null;
        }
    }

    private async Task HandleWatchMessageAsync(ClientWebSocket watchWs, byte[] buf, int count,
        NicovideoState state, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(buf.AsMemory(0, count));
        if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;

        switch (typeProp.GetString())
        {
            case "ping":
                await SendTextAsync(watchWs, """{"type":"pong"}""", ct);
                break;

            case "seat":
                if (doc.RootElement.TryGetProperty("data", out var seatData) &&
                    seatData.TryGetProperty("keepIntervalSec", out var iv))
                    state.KeepSeatIntervalSec = Math.Clamp(iv.GetInt32(), 10, 300);
                break;

            case "serverTime":
                if (doc.RootElement.TryGetProperty("data", out var stData) &&
                    stData.TryGetProperty("currentMs", out var cur) &&
                    DateTime.TryParse(cur.GetString(), out var dt))
                {
                    state.ServerUnixTime = dt.ToUniversalTime() - DateTime.UnixEpoch;
                }
                break;

            case "messageServer":
                if (doc.RootElement.TryGetProperty("data", out var msData))
                {
                    if (msData.TryGetProperty("viewUri", out var vu) && state.ViewUri == null)
                    {
                        state.ViewUri = vu.GetString();
                        _logger.LogInformation("[{Channel}] messageServer受信: {Uri}", _channel, state.ViewUri);
                        ResetReconnectBackoff();
                    }
                    if (msData.TryGetProperty("vposBaseTime", out var vbt) &&
                        DateTime.TryParse(vbt.GetString(), out var vposBase))
                    {
                        var vposBaseUtc = vposBase.ToUniversalTime();
                        var vposBaseUnix = vposBaseUtc - DateTime.UnixEpoch;
                        state.ThreadId = $"{_channel}_{(long)vposBaseUnix.TotalSeconds}";
                        Interlocked.Exchange(ref _upstreamVposBaseTicks,
                            new DateTimeOffset(vposBaseUtc, TimeSpan.Zero).UtcTicks);
                    }
                }
                break;

            case "disconnect":
            case "reconnect":
                await watchWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                break;
        }
    }

    private static byte[] BuildChatJson(string threadId, Chat chat, TimeSpan at)
    {
        var mail = BuildMail(chat.modifier);
        var userId = chat.raw_user_id != 0 ? chat.raw_user_id.ToString() : (chat.hashed_user_id ?? "");
        var dateSeconds = (long)at.TotalSeconds;
        var dateUsec = at.Milliseconds * 1000 + at.Microseconds;
        var premium = chat.account_status == Chat.AccountStatus.Premium ? 1 : 0;
        var anonymity = chat.raw_user_id == 0 ? 1 : 0;

        var sb = new StringBuilder();
        sb.Append("{\"chat\":{");
        sb.Append($"\"thread\":{JsonSerializer.Serialize(threadId, NicovideoJsonContext.Default.String)},");
        sb.Append($"\"no\":{chat.no},");
        sb.Append($"\"vpos\":{chat.vpos},");
        sb.Append($"\"date\":{dateSeconds},");
        sb.Append($"\"date_usec\":{dateUsec},");
        sb.Append($"\"user_id\":{JsonSerializer.Serialize(userId, NicovideoJsonContext.Default.String)},");
        sb.Append($"\"premium\":{premium},");
        sb.Append($"\"anonymity\":{anonymity},");
        sb.Append($"\"content\":{JsonSerializer.Serialize(chat.content ?? "", NicovideoJsonContext.Default.String)}");
        if (mail.Length > 0)
            sb.Append($",\"mail\":{JsonSerializer.Serialize(mail, NicovideoJsonContext.Default.String)}");
        sb.Append("}}");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string BuildMail(Chat.Modifier? mod)
    {
        if (mod == null) return "";
        var parts = new StringBuilder();
        if (mod.full_color != null)
            parts.Append($" #{((mod.full_color.r << 16 | mod.full_color.g << 8 | mod.full_color.b) & 0xFFFFFF):x6}");
        else if (mod.ShouldSerializenamed_color())
            parts.Append($" {mod.named_color}");
        if (mod.position != default) parts.Append($" {mod.position}");
        if (mod.size != default) parts.Append($" {mod.size}");
        if (mod.font != default) parts.Append($" {mod.font}");
        if (mod.opacity != default) parts.Append($" {mod.opacity}");
        return parts.Length > 0 ? parts.ToString().Substring(1).ToLowerInvariant() : "";
    }

    private async Task<Stream> FetchEntryStreamAsync(string url, CancellationToken ct)
    {
        _logger.LogDebug("[{Channel}] entry GET: {Url}", _channel, url);
        var response = await _streamClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[{Channel}] entry HTTP {Status}: {Body}", _channel, (int)response.StatusCode, body);
            if ((int)response.StatusCode == 400)
                throw new EntryStream400Exception();
            response.EnsureSuccessStatusCode();
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    private sealed class EntryStream400Exception : Exception { }
    private static async Task<MemoryStream?> ReadProtoBufChunkAsync(Stream s, MemoryStream ms, byte[] buf, CancellationToken ct)
    {
        ms.SetLength(0);
        // varint読み取り
        int shift = 0, len = 0;
        while (shift < 35)
        {
            if (await s.ReadAsync(buf, 0, 1, ct) <= 0) return null;
            len |= (buf[0] & 0x7F) << shift;
            if ((buf[0] & 0x80) == 0) break;
            shift += 7;
        }
        if (len <= 0 || len > MaxChunkSize) return null;

        // 本体読み取り
        ms.SetLength(0);
        int remaining = len;
        while (remaining > 0)
        {
            int read = await s.ReadAsync(buf, 0, Math.Min(remaining, buf.Length), ct);
            if (read <= 0) return null;
            ms.Write(buf, 0, read);
            remaining -= read;
        }
        ms.Position = 0;
        return ms;
    }

    private sealed class NicovideoState
    {
        public string ActiveLvId { get; set; } = "";
        public string? ViewUri { get; set; }
        public string? NextAt { get; set; } = "now";
        public string ThreadId { get; set; } = "";
        public TimeSpan ServerUnixTime { get; set; } = TimeSpan.Zero;
        public int KeepSeatIntervalSec { get; set; } = 0;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class NicovideoJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
