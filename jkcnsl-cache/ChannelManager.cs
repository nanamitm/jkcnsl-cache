using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace jkcnsl_cache;

public class ChannelManager
{
    private readonly IConfiguration _config;
    private readonly ILogger<ChannelManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly NicovideoSearchService _searchService;
    private readonly LocalStreamConnectionLimiter _localStreamLimiter;
    private readonly ChannelCatalog _channelCatalog;
    private readonly MetricsService _metrics;
    private readonly CommentStorageService _commentStorage;
    private readonly ConcurrentDictionary<string, UpstreamChannelBase> _channels = new();

    private static readonly HttpClient _watchScrapeClient = CreateWatchScrapeClient();
    private static HttpClient CreateWatchScrapeClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            UseCookies = false,
        });
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        client.DefaultRequestHeaders.Add("Accept", "*/*");
        return client;
    }

    public ChannelManager(IConfiguration config, ILogger<ChannelManager> logger, ILoggerFactory loggerFactory,
        NicovideoSearchService searchService, LocalStreamConnectionLimiter localStreamLimiter,
        ChannelCatalog channelCatalog, MetricsService metrics, CommentStorageService commentStorage)
    {
        _config = config;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _searchService = searchService;
        _localStreamLimiter = localStreamLimiter;
        _channelCatalog = channelCatalog;
        _metrics = metrics;
        _commentStorage = commentStorage;
    }

    public DateTimeOffset? GetChannelScheduled(string channel) =>
        ResolveChannel(channel) is { } canonical && _channels.TryGetValue(canonical, out var ch)
            ? ch.ScheduledStartUtc
            : null;

    public bool IsChannelScheduled(string channel) =>
        ResolveChannel(channel) is { } canonical && _channels.TryGetValue(canonical, out var ch) && ch.IsScheduled;

    public (string Status, string? StatusText) GetChannelStatus(string channel)
    {
        var canonical = ResolveChannel(channel);
        if (canonical == null || !_channels.TryGetValue(canonical, out var ch))
            return ("idle", null);
        return (ch.Status, ch.StatusText);
    }

    public bool IsKnownChannel(string channel) =>
        ResolveChannel(channel) != null;

    public bool IsConfiguredChannel(string channel) =>
        !string.IsNullOrEmpty(_config[$"CacheServer:Channels:{channel}"]);

    public async Task HandleWatchSessionAsync(string channel, WebSocket ws, CancellationToken ct)
    {
        _metrics.WatchConnected();
        try
        {
            var canonical = ResolveChannel(channel)!;
            var upstreamValue = _config[$"CacheServer:Channels:{canonical}"]!;
            var isLocalStream = IsLocalStreamValue(upstreamValue);
            var publicBase = (_config["CacheServer:PublicBaseUrl"] ?? "ws://localhost:5000").TrimEnd('/');
            var commentUri = $"{publicBase}/comment/{channel}";
            var watchKeepIntervalSec = Math.Max(5,
                _config.GetValue<int>("CacheServer:WatchKeepIntervalSeconds", 30));
            var watchIdleTimeoutSec = Math.Max(watchKeepIntervalSec * 2,
                _config.GetValue<int>("CacheServer:WatchIdleTimeoutSeconds", 90));
            var watchIdleTimeout = TimeSpan.FromSeconds(watchIdleTimeoutSec);
            var upstream = GetOrCreateChannel(canonical, upstreamValue);
            upstream.EnsureRunning();
            var threadId = upstream.GetDownstreamThreadId(channel);
            // 上流の実際の vposBaseTime があればそれを使う（vpos 計算が正確になる）
            var upstreamVposBase = upstream.VposBaseTime;
            var vposBase = upstreamVposBase.HasValue
                ? upstreamVposBase.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss+00:00")
                : DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss+00:00");
            var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");

            // startWatching を受け取り commentable / cookie を確認する
            var buf = new byte[4096];
            bool commentable = false;
            string? clientCookie = null;
            DateTimeOffset lastLocalPostUtc = DateTimeOffset.MinValue;
            var localUserId = $"local-{Random.Shared.NextInt64(0x100000000L):x8}";
            var localPostInterval = TimeSpan.FromMilliseconds(Math.Max(0,
                _config.GetValue<int>("CacheServer:LocalStream:PostIntervalMilliseconds", 1000)));
            // ニコニコへの投稿は1セッション内でも一定の間隔を空ける（IP BAN防止）
            var nicovideoPostInterval = TimeSpan.FromMilliseconds(Math.Max(0,
                _config.GetValue<int>("CacheServer:NicovideoPostIntervalMilliseconds", 500)));
            DateTimeOffset lastNicovideoPostUtc = DateTimeOffset.MinValue;
            try
            {
                var r = await ReceiveWatchAsync(ws, buf, watchIdleTimeout, ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    return;
                }
                try
                {
                    using var doc = JsonDocument.Parse(buf.AsMemory(0, r.Count));
                    if (doc.RootElement.TryGetProperty("data", out var data))
                    {
                        if (data.TryGetProperty("room", out var room) &&
                            room.TryGetProperty("commentable", out var cp))
                            commentable = cp.GetBoolean();
                        if (data.TryGetProperty("cookie", out var ck))
                            clientCookie = ck.GetString();
                    }
                }
                catch { }
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("[{Ch}] watch初期メッセージ未受信のため切断します: timeoutSeconds={T}",
                    channel, watchIdleTimeoutSec);
                return;
            }
            catch (OperationCanceledException) { return; }

            // seat → serverTime → room の順に応答
            if (isLocalStream || upstream.IsLocalFallbackActive)
                commentable = true;

            _logger.LogDebug("[{Ch}] watch接続 commentable={C} vposBase={V}",
                channel, commentable, vposBase);
            await SendTextAsync(ws, $$$"""{"type":"seat","data":{"keepIntervalSec":{{{watchKeepIntervalSec}}}}}""", ct);
            await SendTextAsync(ws, $$$"""{"type":"serverTime","data":{"currentMs":"{{{now}}}"}}""", ct);
            await SendTextAsync(ws,
                $$$"""{"type":"room","data":{"messageServer":{"uri":"{{{commentUri}}}"},"threadId":"{{{threadId}}}","yourPostKey":"","vposBaseTime":"{{{vposBase}}}"}}""",
                ct);

            // メッセージループ: keepSeat は生存確認、postComment は上流に転送
            try
            {
                using var msgStream = new MemoryStream();
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ReceiveWatchAsync(ws, buf, watchIdleTimeout, ct);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    if (result.MessageType != WebSocketMessageType.Text)
                    {
                        msgStream.SetLength(0);
                        continue;
                    }
                    msgStream.Write(buf, 0, result.Count);
                    if (!result.EndOfMessage) continue;

                    var msgLen = (int)msgStream.Length;
                    var msgBytes = msgStream.GetBuffer();
                    msgStream.SetLength(0);

                    string? msgType = null;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(msgBytes.AsMemory(0, msgLen));
                        if (doc.RootElement.TryGetProperty("type", out var tp))
                            msgType = tp.GetString();
                    }
                    catch { }

                    if (msgType == "keepSeat")
                        continue;

                    if (msgType == "postComment")
                    {
                        _metrics.RecordPostAttempt();
                        var isLocalPostTarget = isLocalStream || upstream.IsLocalFallbackActive;
                        if (isLocalPostTarget)
                            commentable = true;
                        if (isLocalPostTarget && DateTimeOffset.UtcNow - lastLocalPostUtc < localPostInterval)
                        {
                            await ws.SendAsync(
                                "{\"type\":\"error\",\"data\":{\"code\":\"POST_TOO_FAST\"}}"u8.ToArray(),
                                WebSocketMessageType.Text, true, ct);
                            _metrics.RecordPostResult("POST_TOO_FAST");
                            continue;
                        }
                        if (!isLocalPostTarget && DateTimeOffset.UtcNow - lastNicovideoPostUtc < nicovideoPostInterval)
                        {
                            await ws.SendAsync(
                                "{\"type\":\"error\",\"data\":{\"code\":\"POST_TOO_FAST\"}}"u8.ToArray(),
                                WebSocketMessageType.Text, true, ct);
                            _metrics.RecordPostResult("POST_TOO_FAST");
                            continue;
                        }

                        if (!commentable)
                        {
                            _logger.LogWarning("[{Ch}] commentable=false のため投稿を拒否", channel);
                            _metrics.RecordPostResult("COMMENT_POST_NOT_ALLOWED");
                            continue;
                        }

                        ReadOnlyMemory<byte> response;
                        var watchTarget = upstream.WatchTarget;
                        if (!isLocalPostTarget && !string.IsNullOrEmpty(clientCookie) && !string.IsNullOrEmpty(watchTarget))
                        {
                            // per-client NicoNico セッション経由で投稿
                            _logger.LogDebug("[{Ch}] per-client投稿: target={T}", channel, watchTarget);
                            response = await PostViaNicovideoAsync(
                                watchTarget, clientCookie, msgBytes.AsMemory(0, msgLen), _logger, channel, ct);
                        }
                        else
                        {
                            // 共有 upstream セッション経由で投稿（NX-Jikkyo 等）
                            var postJson = isLocalPostTarget
                                ? AddLocalUserId(msgBytes.AsMemory(0, msgLen), localUserId)
                                : msgBytes.AsMemory(0, msgLen);
                            response = await upstream.PostCommentAsync(postJson, ct);
                        }
                        if (isLocalPostTarget)
                            lastLocalPostUtc = DateTimeOffset.UtcNow;
                        else
                            lastNicovideoPostUtc = DateTimeOffset.UtcNow;

                        _metrics.RecordPostResult(ParsePostResultCode(response));
                        _logger.LogDebug("[{Ch}] postComment応答: {R}", channel,
                            Encoding.UTF8.GetString(response.Span));
                        await ws.SendAsync(response, WebSocketMessageType.Text, true, ct);
                    }
                }
            }
            catch (TimeoutException)
            {
                _logger.LogDebug("[{Ch}] watch無通信タイムアウトのため切断します: timeoutSeconds={T}",
                    channel, watchIdleTimeoutSec);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (InvalidOperationException) { }

            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
                catch { }
            }
        }
        finally
        {
            _metrics.WatchDisconnected();
        }
    }

    private static async Task<WebSocketReceiveResult> ReceiveWatchAsync(
        WebSocket ws, byte[] buffer, TimeSpan idleTimeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(idleTimeout);
        try
        {
            return await ws.ReceiveAsync(buffer, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    public async Task HandleCommentSessionAsync(string channel, WebSocket ws, CancellationToken ct)
    {
        _metrics.CommentConnected();
        try
        {
        var canonical = ResolveChannel(channel)!;
        var upstreamValue = _config[$"CacheServer:Channels:{canonical}"] ?? "";
        var upstream = GetOrCreateChannel(canonical, upstreamValue);
        upstream.EnsureRunning();

        await upstream.AddClientAndWaitAsync(ws, channel, ct);

        // 監視専用チャンネルは停止しない。クライアントが全員切断した通常チャンネルは停止
        if (upstream.ClientCount == 0 && !upstream.IsMonitored &&
            _channels.TryRemove(new KeyValuePair<string, UpstreamChannelBase>(canonical, upstream)))
            _ = upstream.StopAsync();
        }
        finally
        {
            _metrics.CommentDisconnected();
        }
    }

    // watchdog 用: 監視中チャンネルのうち、ローカル待避が一定時間続いているものを検出するためのスナップショット
    public IReadOnlyList<(string Channel, string Status, DateTimeOffset? FallbackSinceUtc, bool IsScheduled)> GetMonitoredChannelFallbackStates() =>
        _channels
            .Where(kv => kv.Value.IsMonitored)
            .Select(kv => (kv.Key, kv.Value.Status, kv.Value.LocalFallbackSinceUtc, kv.Value.IsScheduled))
            .ToArray();

    // watchdog 用: fallbackLocal に固まって復帰しないチャンネルを強制再起動する
    public Task RestartChannelAsync(string channel) =>
        _channels.TryGetValue(channel, out var ch) ? ch.RestartAsync() : Task.CompletedTask;

    public void StartMonitoring(string channel)
    {
        var canonical = ResolveChannel(channel);
        if (canonical == null) return;
        var upstreamValue = _config[$"CacheServer:Channels:{canonical}"];
        if (string.IsNullOrEmpty(upstreamValue)) return;
        var upstream = GetOrCreateChannel(canonical, upstreamValue);
        upstream.IsMonitored = true;
        upstream.EnsureRunning();
    }

    public (bool Running, string Type, string? CurrentTarget, int Force, int Viewers, long TotalComments, long LastResNo) GetChannelFullStats(string channel)
    {
        var canonical = ResolveChannel(channel) ?? channel;
        var upstreamValue = _config[$"CacheServer:Channels:{canonical}"] ?? "";
        var type = upstreamValue switch
        {
            _ when upstreamValue.StartsWith("nicovideo:ch", StringComparison.Ordinal) => "公式",
            "nicovideo:" => "非公式",
            _ when IsLocalStreamValue(upstreamValue) => "ローカル",
            _ when upstreamValue.StartsWith("wss://", StringComparison.Ordinal) ||
                   upstreamValue.StartsWith("ws://", StringComparison.Ordinal) => "避難所",
            _ => "-"
        };
        if (!_channels.TryGetValue(canonical, out var ch))
            return (false, type, null, 0, 0, 0, 0);
        var viewers = GetViewerCount(canonical, upstreamValue, ch);
        return (ch.IsRunning, type, ch.CurrentTarget, ch.Force, viewers, ch.TotalComments, ch.LastResNo);
    }

    public (int Force, int Viewers, long TotalComments, long LastResNo) GetChannelStats(string channel)
    {
        var canonical = ResolveChannel(channel) ?? channel;
        if (!_channels.TryGetValue(canonical, out var ch))
            return (0, 0, 0, 0);
        var upstreamValue = _config[$"CacheServer:Channels:{canonical}"];
        var viewers = GetViewerCount(canonical, upstreamValue, ch);
        return (ch.Force, viewers, ch.TotalComments, ch.LastResNo);
    }

    private int GetViewerCount(string canonical, string? upstreamValue, UpstreamChannelBase channel)
    {
        if (IsLocalStreamValue(upstreamValue))
            return channel.ClientCount;

        // 同じ上流を持つエイリアスチャンネル（ch2646436 等）のクライアントも合算する
        return _channels
            .Where(kv => _config[$"CacheServer:Channels:{kv.Key}"] == upstreamValue)
            .Sum(kv => kv.Value.ClientCount);
    }

    public (int Force, int Viewers, long TotalComments, long LastResNo) GetAggregatedStats(IEnumerable<string> channels)
    {
        var force = 0;
        var viewers = 0;
        long totalComments = 0;
        long lastResNo = 0;

        foreach (var channel in channels.Where(IsConfiguredChannel))
        {
            var stats = GetChannelStats(channel);
            force += stats.Force;
            viewers += stats.Viewers;
            totalComments += stats.TotalComments;
            lastResNo = Math.Max(lastResNo, stats.LastResNo);
        }

        return (force, viewers, totalComments, lastResNo);
    }

    public string? ResolveChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel)) return null;

        if (channel.StartsWith("ch", StringComparison.Ordinal))
        {
            var officialValue = "nicovideo:" + channel;
            var officialChannel = _channelCatalog.All.FirstOrDefault(info =>
                string.Equals(_config[$"CacheServer:Channels:{info.Video}"], officialValue, StringComparison.Ordinal));
            if (officialChannel != null) return officialChannel.Video;
        }

        if (IsConfiguredChannel(channel)) return channel;

        if (Regex.IsMatch(channel, @"^jk\d+$", RegexOptions.CultureInvariant))
        {
            var refugeChannel = channel + "r";
            if (IsConfiguredChannel(refugeChannel)) return refugeChannel;
        }

        return null;
    }

    public async Task StopMonitoredAsync()
    {
        var tasks = _channels.Values
            .Where(c => c.IsMonitored)
            .Select(c => c.StopAsync());
        await Task.WhenAll(tasks);
    }

    // "nicovideo:{lvId}" なら NicovideoUpstreamChannel、それ以外は UpstreamChannel (避難所)
    // lvId が空（"nicovideo:"）の非公式チャンネルには NicovideoSearchService を渡す
    private UpstreamChannelBase GetOrCreateChannel(string channel, string upstreamValue) =>
        _channels.GetOrAdd(channel, _ =>
        {
            UpstreamChannelBase ch;
            if (IsLocalStreamValue(upstreamValue))
            {
                ch = new LocalStreamChannel(channel, _config,
                    _loggerFactory.CreateLogger<LocalStreamChannel>(),
                    _localStreamLimiter,
                    _metrics);
            }
            else if (upstreamValue.StartsWith("nicovideo:", StringComparison.Ordinal))
            {
                var lvId = upstreamValue["nicovideo:".Length..];
                ch = new NicovideoUpstreamChannel(channel, lvId, _config,
                    _loggerFactory.CreateLogger<NicovideoUpstreamChannel>(),
                    TimeSpan.FromMinutes(_config.GetValue<int>("CacheServer:NoStreamCheckIntervalMinutes", 30)),
                    _metrics,
                    string.IsNullOrEmpty(lvId) ? _searchService : null);
            }
            else
            {
                ch = new UpstreamChannel(channel, upstreamValue, _config,
                    _loggerFactory.CreateLogger<UpstreamChannel>(),
                    _metrics);
            }
            ch.CommentStorage = _commentStorage;
            return ch;
        });

    private static bool IsLocalStreamValue(string? upstreamValue) =>
        upstreamValue?.StartsWith("localstream:", StringComparison.OrdinalIgnoreCase) == true;

    private static Task SendTextAsync(WebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, ct);

    private static ReadOnlyMemory<byte> AddLocalUserId(ReadOnlyMemory<byte> json, string userId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.NameEquals("data") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("data");
                    writer.WriteStartObject();
                    foreach (var dataProp in prop.Value.EnumerateObject())
                        dataProp.WriteTo(writer);
                    writer.WriteString("localUserId", userId);
                    writer.WriteEndObject();
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            return ms.ToArray();
        }
        catch
        {
            return json;
        }
    }

    private static string? ParsePostResultCode(ReadOnlyMemory<byte> response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response);
            var type = doc.RootElement.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (string.Equals(type, "postCommentResult", StringComparison.OrdinalIgnoreCase))
                return "OK";
            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) &&
                doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("code", out var code))
                return code.GetString() ?? "ERROR";
        }
        catch { }
        return "ERROR";
    }

    // NicoNico の watch ページをスクレイピングして WebSocket URL を取得
    private static async Task<string?> ScrapeNicoWatchUrlAsync(
        string watchTarget, string cookie, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://live.nicovideo.jp/watch/{watchTarget}");
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var response = await _watchScrapeClient.SendAsync(request, ct);
            var html = await response.Content.ReadAsStringAsync(ct);
            var m = Regex.Match(html, @"<script[^>]* id=""embedded-data""[^>]*>");
            if (!m.Success) return null;
            m = Regex.Match(m.Value, @" data-props=""([^""]*)""");
            if (!m.Success) return null;
            using var doc = JsonDocument.Parse(WebUtility.HtmlDecode(m.Groups[1].Value));
            if (!doc.RootElement.TryGetProperty("site", out var site)) return null;
            if (!site.TryGetProperty("relive", out var relive)) return null;
            if (!relive.TryGetProperty("webSocketUrl", out var wsUrl)) return null;
            var url = wsUrl.GetString();
            return Regex.IsMatch(url ?? "", @"^wss://[0-9A-Za-z.-]+\.nicovideo\.jp/") ? url : null;
        }
        catch { ct.ThrowIfCancellationRequested(); return null; }
    }

    // per-client NicoNico watch セッションを使って postComment を転送
    private static async Task<ReadOnlyMemory<byte>> PostViaNicovideoAsync(
        string watchTarget, string cookie, ReadOnlyMemory<byte> postJson,
        ILogger logger, string channel, CancellationToken ct)
    {
        // watch URL をスクレイピング（cookie 付きで user-specific token を取得）
        var watchUrl = await ScrapeNicoWatchUrlAsync(watchTarget, cookie, ct);
        if (watchUrl == null)
        {
            logger.LogWarning("[{Ch}] watch URL の取得に失敗しました", channel);
            return "{\"type\":\"error\",\"data\":{\"code\":\"COMMENT_POST_NOT_ALLOWED\"}}"u8.ToArray();
        }

        using var watchWs = new ClientWebSocket();
        watchWs.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");
        watchWs.Options.SetRequestHeader("Origin", "https://live.nicovideo.jp");
        watchWs.Options.SetRequestHeader("Cache-Control", "no-cache");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Dest", "empty");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Mode", "websocket");
        watchWs.Options.SetRequestHeader("Sec-Fetch-Site", "same-site");
        watchWs.Options.SetRequestHeader("Cookie", cookie);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            await watchWs.ConnectAsync(new Uri(watchUrl), timeoutCts.Token);
            await SendWsTextAsync(watchWs,
                """{"type":"startWatching","data":{"reconnect":false}}""", timeoutCts.Token);

            var buf = new byte[16384];
            int bufCount = 0;
            bool seatReceived = false;

            // seat を待つ（新APIは room ではなく seat でセッション確立を示す）
            while (!seatReceived)
            {
                var r = await watchWs.ReceiveAsync(
                    new ArraySegment<byte>(buf, bufCount, buf.Length - bufCount), timeoutCts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    return "{\"type\":\"error\",\"data\":{\"code\":\"COMMENT_POST_NOT_ALLOWED\"}}"u8.ToArray();
                bufCount += r.Count;
                if (!r.EndOfMessage) continue;

                using var doc = JsonDocument.Parse(buf.AsMemory(0, bufCount));
                var msgCount = bufCount;
                bufCount = 0;
                var type = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                switch (type)
                {
                    case "ping":
                        await SendWsTextAsync(watchWs, """{"type":"pong"}""", timeoutCts.Token);
                        break;
                    case "error":
                        return buf.AsMemory(0, msgCount).ToArray();
                    case "seat":
                        seatReceived = true;
                        break;
                }
            }

            // postComment を送信
            await watchWs.SendAsync(postJson, WebSocketMessageType.Text, true, timeoutCts.Token);

            // postCommentResult / error を待つ
            bufCount = 0;
            while (true)
            {
                var r = await watchWs.ReceiveAsync(
                    new ArraySegment<byte>(buf, bufCount, buf.Length - bufCount), timeoutCts.Token);
                if (r.MessageType == WebSocketMessageType.Close)
                    return "{\"type\":\"error\",\"data\":{\"code\":\"COMMENT_POST_NOT_ALLOWED\"}}"u8.ToArray();
                bufCount += r.Count;
                if (!r.EndOfMessage) continue;

                using var doc = JsonDocument.Parse(buf.AsMemory(0, bufCount));
                var msgCount = bufCount;
                bufCount = 0;
                var type = doc.RootElement.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                if (type == "ping")
                    await SendWsTextAsync(watchWs, """{"type":"pong"}""", timeoutCts.Token);
                else if (type == "postCommentResult" || type == "error")
                    return buf.AsMemory(0, msgCount).ToArray();
            }
        }
        catch (OperationCanceledException)
        {
            return "{\"type\":\"error\",\"data\":{\"code\":\"TIMEOUT\"}}"u8.ToArray();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Ch}] per-client投稿セッションエラー", channel);
            return "{\"type\":\"error\",\"data\":{\"code\":\"COMMENT_POST_NOT_ALLOWED\"}}"u8.ToArray();
        }
    }

    private static Task SendWsTextAsync(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),
            WebSocketMessageType.Text, true, ct);
}
