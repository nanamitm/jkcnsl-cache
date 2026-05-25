using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;

namespace jkcnsl_cache;

// ScheduledStartUtc が null = onair（即接続可）、non-null = 配信予定の開始時刻(UTC)
public record NicovideoSearchResult(string LvId, DateTime? ScheduledStartUtc, bool IsReserved = false);

public class NicovideoSearchService
{
    private readonly ILogger<NicovideoSearchService> _logger;
    private readonly TimeSpan _interval;
    private readonly HttpClient _http;

    // channel → 公式 ch ID（例 "ch2646436"）。対応する公式チャンネルがなければ null
    private readonly IReadOnlyDictionary<string, string?> _officialChIds;
    // channel → マッチに使うチャンネル名（例 "BS日テレ"）
    private readonly IReadOnlyDictionary<string, string> _searchNames;
    // 配信ページの時刻を UTC に変換するタイムゾーン
    private readonly TimeZoneInfo _broadcastTz;

    // 次の検索バッチを待つ TCS。volatile + Interlocked.Exchange で書き込み、Volatile.Read で読み取り
    private volatile TaskCompletionSource<IReadOnlyDictionary<string, NicovideoSearchResult?>> _nextBatch =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // 直近の検索結果キャッシュ（スリープ中にバッチを逃したチャンネルが即座に取得できるよう）
    private sealed record CachedBatch(IReadOnlyDictionary<string, NicovideoSearchResult?> Results, DateTimeOffset At);
    private volatile CachedBatch? _lastBatch;
    private readonly Dictionary<string, SearchLogState?> _lastLoggedStates = new();

    private sealed record SearchLogState(string LvId, bool IsReserved, DateTime? ScheduledStartUtc);

    public NicovideoSearchService(IConfiguration config, ILogger<NicovideoSearchService> logger,
        ChannelCatalog channelCatalog)
    {
        _logger = logger;
        _interval = TimeSpan.FromMinutes(config.GetValue<double>("CacheServer:NoStreamCheckIntervalMinutes", 30));

        var tzId = config.GetValue<string>("CacheServer:BroadcastTimeZone");
        _broadcastTz = string.IsNullOrEmpty(tzId)
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(tzId);
        _logger.LogInformation("[SearchService] 配信時刻のタイムゾーン: {Tz}", _broadcastTz.DisplayName);

        _http = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        }) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        _http.DefaultRequestHeaders.Add("Accept", "*/*");

        var officialChIds = new Dictionary<string, string?>();
        var searchNames = new Dictionary<string, string>();

        foreach (var ch in config.GetSection("CacheServer:Channels").GetChildren())
        {
            if (!ch.Key.StartsWith("co", StringComparison.Ordinal)) continue;

            var jkId = "jk" + ch.Key["co".Length..];  // "co141" → "jk141"

            // 対応する公式チャンネルの ch ID を config から取得
            var officialValue = config[$"CacheServer:Channels:{jkId}"];
            string? officialChId = null;
            if (officialValue != null && officialValue.StartsWith("nicovideo:ch", StringComparison.Ordinal))
                officialChId = officialValue["nicovideo:".Length..];  // → "ch2646436"

            var channelInfo = channelCatalog.All.FirstOrDefault(c => c.Video == jkId);
            if (channelInfo == null) continue;

            officialChIds[ch.Key] = officialChId;
            searchNames[ch.Key] = channelInfo.Name;
        }

        _officialChIds = officialChIds;
        _searchNames = searchNames;
    }

    // NicovideoUpstreamChannel から呼ばれる：次の検索バッチ結果が来るまでブロック
    // スリープ中にバッチを逃した場合は直近キャッシュを即座に返す。
    // ただし「配信なし(null)」は即返ししない。チャンネル側が短周期で再確認を繰り返し、
    // メンテナンス時などにログと接続試行が増えるため、次の検索バッチまで待たせる。
    public async Task<NicovideoSearchResult?> WaitForNextResultAsync(string channel, string? failedLvId, CancellationToken ct)
    {
        var cached = _lastBatch;
        if (cached != null && DateTimeOffset.UtcNow - cached.At < _interval)
        {
            cached.Results.TryGetValue(channel, out var cachedResult);
            // スクレイピングに失敗した lv と同じキャッシュはスキップして次のバッチを待つ
            if (cachedResult != null && (failedLvId == null || cachedResult.LvId != failedLvId))
                return cachedResult;
        }

        var results = await _nextBatch.Task.WaitAsync(ct);
        results.TryGetValue(channel, out var result);
        return result;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (_searchNames.Count == 0) return;

        _logger.LogInformation("[SearchService] 開始 (間隔={Min}分, 対象={Channels})",
            (int)_interval.TotalMinutes, string.Join(", ", _searchNames.Keys));

        // 初回は即時実行
        await RunCycleAsync(ct);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(ct))
            await RunCycleAsync(ct);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, NicovideoSearchResult?> results;
        try
        {
            results = await SearchAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SearchService] 検索サイクルでエラー");
            results = _searchNames.Keys.ToDictionary(k => k, _ => (NicovideoSearchResult?)null);
        }

        // キャッシュを更新してから TCS を発火
        _lastBatch = new CachedBatch(results, DateTimeOffset.UtcNow);
        var tcs = Interlocked.Exchange(ref _nextBatch,
            new TaskCompletionSource<IReadOnlyDictionary<string, NicovideoSearchResult?>>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        tcs.SetResult(results);
    }

    private async Task<IReadOnlyDictionary<string, NicovideoSearchResult?>> SearchAllAsync(CancellationToken ct)
    {
        var results = _searchNames.Keys.ToDictionary(k => k, _ => (NicovideoSearchResult?)null);

        // 1リクエストで全 onair / reserved 結果を取得
        var onairPairs = await FetchPairsAsync("onair", ct);
        var reservedPairs = await FetchPairsAsync("reserved", ct);

        _logger.LogDebug("[SearchService] onair={OnairCount}件, reserved={ReservedCount}件",
            onairPairs.Count, reservedPairs.Count);

        foreach (var (channel, name) in _searchNames)
        {
            // onair から一致するものを検索
            foreach (var (lvId, title) in onairPairs)
            {
                if (!title.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (_officialChIds[channel] is { } chId &&
                    await IsOfficialStreamAsync(lvId, chId, ct))
                {
                    _logger.LogDebug("[SearchService] [{Channel}] {LvId} は公式配信のためスキップ", channel, lvId);
                    continue;
                }
                results[channel] = new NicovideoSearchResult(lvId, null);
                LogResultChange(channel, results[channel], title);
                break;
            }
            if (results[channel] != null) continue;

            // reserved から一致するものを検索
            foreach (var (lvId, title) in reservedPairs)
            {
                if (!title.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (_officialChIds[channel] is { } chId &&
                    await IsOfficialStreamAsync(lvId, chId, ct))
                {
                    _logger.LogDebug("[SearchService] [{Channel}] {LvId} は公式配信のためスキップ", channel, lvId);
                    continue;
                }
                var startTime = await GetScheduledTimeAsync(lvId, ct);
                if (!startTime.HasValue)
                {
                    // watch ページで取得できない場合はタイトルの日付（年月日）を使う
                    var dm = Regex.Match(title, @"(\d{4})年(\d{1,2})月(\d{1,2})日");
                    if (dm.Success && DateTime.TryParseExact(
                            $"{dm.Groups[1].Value}/{dm.Groups[2].Value}/{dm.Groups[3].Value}",
                            "yyyy/M/d", null,
                            System.Globalization.DateTimeStyles.None, out var jst))
                        startTime = TimeZoneInfo.ConvertTimeToUtc(jst, _broadcastTz);
                }
                results[channel] = new NicovideoSearchResult(lvId, startTime, IsReserved: true);
                LogResultChange(channel, results[channel], title);
                break;
            }

            if (results[channel] == null)
            {
                LogResultChange(channel, null, null);
                _logger.LogDebug("[SearchService] [{Channel}] 配信なし・予定なし", channel);
            }
        }

        return results;
    }

    // 【ニコニコ実況】で検索し (lvId, title) ペアの一覧を返す
    private async Task<List<(string LvId, string Title)>> FetchPairsAsync(string status, CancellationToken ct)
    {
        try
        {
            var url = $"https://live.nicovideo.jp/search?keyword={Uri.EscapeDataString("【ニコニコ実況】")}&status={status}";
            var html = await _http.GetStringAsync(url, ct);
            var matches = Regex.Matches(html,
                @"href=""(?:https://live\.nicovideo\.jp)?/watch/(lv\d{8,})[^""]*""[^>]*>\s*([^<\r\n]{3,80})");
            return matches
                .Select(m => (m.Groups[1].Value, m.Groups[2].Value.Trim()))
                .Where(p => !string.IsNullOrEmpty(p.Item2))
                .DistinctBy(p => p.Item1)
                .ToList();
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[SearchService] 検索ページ取得失敗 (status={Status})", status);
            return [];
        }
    }

    private void LogResultChange(string channel, NicovideoSearchResult? result, string? title)
    {
        var next = result == null
            ? null
            : new SearchLogState(result.LvId, result.IsReserved, result.ScheduledStartUtc);

        _lastLoggedStates.TryGetValue(channel, out var previous);
        if (Equals(previous, next))
        {
            if (result != null)
            {
                _logger.LogDebug("[SearchService] [{Channel}] {State}: {LvId} (変化なし)",
                    channel, result.IsReserved ? "reserved" : "onair", result.LvId);
            }
            return;
        }

        _lastLoggedStates[channel] = next;
        if (result == null)
        {
            _logger.LogInformation("[SearchService] [{Channel}] 配信なし・予定なし", channel);
            return;
        }

        if (result.IsReserved)
        {
            _logger.LogInformation("[SearchService] [{Channel}] 配信予定: {LvId} → {Time} JST",
                channel, result.LvId,
                result.ScheduledStartUtc.HasValue ? result.ScheduledStartUtc.Value.AddHours(9).ToString("MM/dd HH:mm") : "時刻不明");
            return;
        }

        _logger.LogInformation("[SearchService] [{Channel}] onair: {LvId} ({Title})",
            channel, result.LvId, title ?? "");
    }

    // lv が officialChId の公式チャンネル配信かどうかを確認（watch ページの embedded-data を参照）
    private async Task<bool> IsOfficialStreamAsync(string lvId, string officialChId, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync($"https://live.nicovideo.jp/watch/{lvId}", ct);
            var match = Regex.Match(html, @"<script[^>]* id=""embedded-data""[^>]*>");
            if (!match.Success) return false;
            match = Regex.Match(match.Value, @" data-props=""([^""]*)""");
            if (!match.Success) return false;
            using var doc = JsonDocument.Parse(HttpUtility.HtmlDecode(match.Groups[1].Value));
            if (!doc.RootElement.TryGetProperty("channel", out var ch)) return false;
            if (!ch.TryGetProperty("id", out var idProp)) return false;
            return idProp.GetString() == officialChId;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[SearchService] 公式チャンネル確認失敗: {LvId}", lvId);
            return false;
        }
    }

    // reserved な lv の配信開始時刻を watch ページから取得（JST 表記 "2026/4/25(土) 17:00開始" をパース）
    private async Task<DateTime?> GetScheduledTimeAsync(string lvId, CancellationToken ct)
    {
        try
        {
            var html = await _http.GetStringAsync($"https://live.nicovideo.jp/watch/{lvId}", ct);
            // 形式1: "2026/4/27(土) 21:00開始"
            var m = Regex.Match(html, @"(\d{4}/\d{1,2}/\d{1,2})\([^)]+\)\s+(\d{2}:\d{2})開始");
            if (m.Success && DateTime.TryParseExact($"{m.Groups[1].Value} {m.Groups[2].Value}",
                    "yyyy/M/d HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var jst1))
                return TimeZoneInfo.ConvertTimeToUtc(jst1, _broadcastTz);
            // 形式2: "2026年4月27日(土) 21:00開始"
            var m2 = Regex.Match(html, @"(\d{4})年(\d{1,2})月(\d{1,2})日[^<]{0,20}(\d{2}:\d{2})開始");
            if (m2.Success && DateTime.TryParseExact(
                    $"{m2.Groups[1].Value}/{m2.Groups[2].Value}/{m2.Groups[3].Value} {m2.Groups[4].Value}",
                    "yyyy/M/d HH:mm", null,
                    System.Globalization.DateTimeStyles.None, out var jst2))
                return TimeZoneInfo.ConvertTimeToUtc(jst2, _broadcastTz);
            return null;
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning(ex, "[SearchService] 配信予定時刻取得失敗: {LvId}", lvId);
            return null;
        }
    }
}
