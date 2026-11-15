using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace jkcnsl_cache.Nichan;

// RecentMomentum: 直近の subject.txt 取得間で増えたレス数から求めた勢い（1 時間あたりのレス数）。
// 観測期間が足りないとき（起動直後・新スレ）は null。
public sealed record NichanThreadInfo(NichanThreadAddress Address, string Title, int ResCount,
    double? RecentMomentum = null);

// subject.txt を板単位で共有キャッシュする。
// livebs2 のように複数チャンネルが同じ板を使っても、取得は SubjectInterval ごとに 1 回で済む。
// 板が 404 になったときは bbsmenu.html で移転先を探す（TVTComment の BoardClient.ResolveMovedBoardAsync と同じ考え方）。
public sealed partial class NichanBoardService
{
    private static readonly Uri BbsMenuUri = new("https://menu.5ch.io/bbsmenu.html");
    // 直近の勢いを求める観測窓。番組中に伸びて今は止まったスレが、全期間の平均で上位に残らないようにする
    internal static readonly TimeSpan MomentumWindow = TimeSpan.FromMinutes(10);
    // これより短い観測期間では直近の勢いを出さない（1 回分の増減で順位が大きく揺れないように）
    internal static readonly TimeSpan MomentumMinSpan = TimeSpan.FromMinutes(2);

    private readonly NichanHttpClient _http;
    private readonly ILogger<NichanBoardService> _logger;
    private readonly ConcurrentDictionary<string, BoardCache> _boards = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Uri> _moved = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _menuLock = new(1, 1);
    private Dictionary<string, Uri> _menu = new(StringComparer.Ordinal);
    private DateTimeOffset _menuExpires = DateTimeOffset.MinValue;

    public NichanBoardService(NichanHttpClient http, ILogger<NichanBoardService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NichanThreadInfo>> GetThreadsAsync(Uri board, CancellationToken ct)
    {
        board = NichanThreadAddress.NormalizeBoard(board);
        var cache = _boards.GetOrAdd(board.AbsoluteUri, _ => new BoardCache());
        await cache.Lock.WaitAsync(ct);
        try
        {
            // 同じ板を見ている他チャンネルが直前に取得していれば、それを使う
            var maxAge = _http.Options.SubjectInterval - TimeSpan.FromSeconds(5);
            if (cache.Threads != null && DateTimeOffset.UtcNow - cache.FetchedAt < maxAge)
                return cache.Threads;

            var actual = _moved.TryGetValue(board.AbsoluteUri, out var moved) ? moved : board;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(actual, "subject.txt"));
                if (cache.LastModified != null && cache.Threads != null)
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", cache.LastModified);
                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode == HttpStatusCode.NotModified && cache.Threads != null)
                {
                    // 更新がなくても「この時点でレス数が変わっていない」という観測として記録する
                    cache.FetchedAt = DateTimeOffset.UtcNow;
                    cache.Threads = WithRecentMomentum(cache, cache.Threads, cache.FetchedAt);
                    return cache.Threads;
                }
                if (response.StatusCode == HttpStatusCode.NotFound && attempt == 0)
                {
                    var resolved = await ResolveMovedBoardAsync(actual, ct);
                    if (resolved != actual)
                    {
                        _logger.LogInformation("5ch 板の移転を検出しました: {From} → {To}", actual, resolved);
                        _moved[board.AbsoluteUri] = resolved;
                        actual = resolved;
                        continue;
                    }
                }
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new NichanException($"subject.txt の取得に失敗しました（HTTP {(int)response.StatusCode}）: {actual}");

                // リダイレクトで移転していれば記録しておく
                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri != null)
                {
                    var finalBoard = NichanThreadAddress.NormalizeBoard(finalUri);
                    if (finalBoard != actual)
                    {
                        _moved[board.AbsoluteUri] = finalBoard;
                        actual = finalBoard;
                    }
                }

                var text = NichanDatParser.ShiftJis.GetString(await response.Content.ReadAsByteArrayAsync(ct));
                var threads = new List<NichanThreadInfo>();
                foreach (var line in text.Split('\n'))
                {
                    var entry = NichanDatParser.ParseSubjectLine(line.TrimEnd('\r'));
                    if (entry == null) continue;
                    var address = new NichanThreadAddress(actual, entry.Key);
                    if (address.IsAnnouncement) continue;
                    threads.Add(new NichanThreadInfo(address, entry.Title, entry.ResCount));
                }
                cache.FetchedAt = DateTimeOffset.UtcNow;
                cache.Threads = WithRecentMomentum(cache, threads, cache.FetchedAt);
                cache.LastModified = response.Content.Headers.LastModified?.ToString("r");
                return cache.Threads;
            }
            throw new NichanException($"板が見つかりません: {board}");
        }
        finally
        {
            cache.Lock.Release();
        }
    }

    // 取得ごとのレス数を記録し、観測窓の中で最も古い記録との差から直近の勢いを付ける。
    // cache.Lock を保持した状態で呼ぶ。
    internal static IReadOnlyList<NichanThreadInfo> WithRecentMomentum(BoardCache cache,
        IReadOnlyList<NichanThreadInfo> threads, DateTimeOffset now)
    {
        var result = new List<NichanThreadInfo>(threads.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var thread in threads)
        {
            var key = thread.Address.Identity;
            seen.Add(key);
            if (!cache.Samples.TryGetValue(key, out var samples))
                cache.Samples[key] = samples = new Queue<(DateTimeOffset At, int ResCount)>();
            samples.Enqueue((now, thread.ResCount));
            while (samples.Count > 1 && now - samples.Peek().At > MomentumWindow)
                samples.Dequeue();

            double? momentum = null;
            var (oldestAt, oldestCount) = samples.Peek();
            var span = now - oldestAt;
            if (span >= MomentumMinSpan)
                momentum = Math.Max(0, thread.ResCount - oldestCount) / span.TotalHours;
            result.Add(thread with { RecentMomentum = momentum });
        }
        // 一覧から消えたスレ（dat 落ち）の記録は捨てる
        foreach (var key in cache.Samples.Keys.Where(k => !seen.Contains(k)).ToArray())
            cache.Samples.Remove(key);
        return result;
    }

    // ステータスページ用: 取得したことのある板の一覧
    public NichanBoardStatusDto[] GetBoardStatuses() =>
        _boards
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new NichanBoardStatusDto(
                kv.Key,
                _moved.TryGetValue(kv.Key, out var moved) ? moved.AbsoluteUri : null,
                kv.Value.Threads?.Count ?? 0,
                kv.Value.Threads == null ? null : kv.Value.FetchedAt))
            .ToArray();

    private async Task<Uri> ResolveMovedBoardAsync(Uri board, CancellationToken ct)
    {
        await _menuLock.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow >= _menuExpires)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, BbsMenuUri);
                using var response = await _http.SendAsync(request, ct);
                if (response.StatusCode != HttpStatusCode.OK)
                    return board;
                var html = NichanDatParser.ShiftJis.GetString(await response.Content.ReadAsByteArrayAsync(ct));
                var menu = new Dictionary<string, Uri>(StringComparer.Ordinal);
                foreach (Match m in HrefRegex().Matches(html))
                {
                    if (!Uri.TryCreate(WebUtility.HtmlDecode(m.Groups[1].Value), UriKind.Absolute, out var uri) ||
                        !NichanThreadAddress.Is5chHost(uri.Host) ||
                        !BoardPathRegex().IsMatch(uri.AbsolutePath))
                        continue;
                    menu[uri.AbsolutePath] = NichanThreadAddress.NormalizeBoard(uri);
                }
                _menu = menu;
                _menuExpires = DateTimeOffset.UtcNow.AddMinutes(30);
            }
            return _menu.TryGetValue(board.AbsolutePath, out var moved) ? moved : board;
        }
        finally
        {
            _menuLock.Release();
        }
    }

    internal sealed class BoardCache
    {
        // スレ（Identity）ごとの取得時刻とレス数の記録。MomentumWindow より古いものは捨てる
        public readonly Dictionary<string, Queue<(DateTimeOffset At, int ResCount)>> Samples =
            new(StringComparer.Ordinal);
        public readonly SemaphoreSlim Lock = new(1, 1);
        public IReadOnlyList<NichanThreadInfo>? Threads;
        public DateTimeOffset FetchedAt = DateTimeOffset.MinValue;
        public string? LastModified;
    }

    [GeneratedRegex(@"href\s*=\s*[""']?(https?://[^\s""'<>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex("^/[a-zA-Z0-9_]+/$")]
    private static partial Regex BoardPathRegex();
}
