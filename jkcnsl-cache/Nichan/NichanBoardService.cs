using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace jkcnsl_cache.Nichan;

public sealed record NichanThreadInfo(NichanThreadAddress Address, string Title, int ResCount);

// subject.txt を板単位で共有キャッシュする。
// livebs2 のように複数チャンネルが同じ板を使っても、取得は SubjectInterval ごとに 1 回で済む。
// 板が 404 になったときは bbsmenu.html で移転先を探す（TVTComment の BoardClient.ResolveMovedBoardAsync と同じ考え方）。
public sealed partial class NichanBoardService
{
    private static readonly Uri BbsMenuUri = new("https://menu.5ch.io/bbsmenu.html");

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
                    cache.FetchedAt = DateTimeOffset.UtcNow;
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
                cache.Threads = threads;
                cache.FetchedAt = DateTimeOffset.UtcNow;
                cache.LastModified = response.Content.Headers.LastModified?.ToString("r");
                return threads;
            }
            throw new NichanException($"板が見つかりません: {board}");
        }
        finally
        {
            cache.Lock.Release();
        }
    }

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

    private sealed class BoardCache
    {
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
