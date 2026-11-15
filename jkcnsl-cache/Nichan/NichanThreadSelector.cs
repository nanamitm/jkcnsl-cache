using System.Globalization;
using System.Text;

namespace jkcnsl_cache.Nichan;

public sealed record NichanSelectedThread(NichanThreadInfo Thread, bool ProgramMatch);

// 番組表による現在の番組と、そこから作ったスレタイ照合キーを保持する。
// 番組が切り替わった後も、前の番組のキーをしばらく有効にする。
public sealed class NichanProgramTracker
{
    // 番組が切り替わった後も、前の番組のスレをこの時間は番組一致として扱う
    private static readonly TimeSpan PreviousProgramGrace = TimeSpan.FromMinutes(5);
    private const string AnimeGenreCode = "0x7";

    private readonly ProgramInfoService? _programs;
    private readonly string _video;
    private readonly object _lock = new();
    private string? _title;
    private ProgramKeys _current = ProgramKeys.Empty;
    private ProgramKeys _previous = ProgramKeys.Empty;
    private DateTimeOffset _previousUntil = DateTimeOffset.MinValue;

    // video: 番組表のキー（jk104 など。jk104n ではない）
    public NichanProgramTracker(ProgramInfoService? programs, string video)
    {
        _programs = programs;
        _video = video;
    }

    public string? Title { get { lock (_lock) return _title; } }
    public IReadOnlyList<string> CurrentKeys { get { lock (_lock) return _current.Keys; } }

    // 番組表から現在の番組を読み、変わっていれば true を返す
    public bool Refresh(DateTimeOffset now)
    {
        var program = _programs?.GetProgram(_video);
        var title = program != null && program.StartAt <= now && now < program.EndAt
            ? program.Title
            : null;
        lock (_lock)
        {
            if (title == _title)
                return false;
            if (_title != null)
            {
                _previous = _current;
                _previousUntil = now + PreviousProgramGrace;
            }
            _title = title;
            _current = new ProgramKeys(
                NichanProgramMatcher.BuildKeys(title),
                program?.GenreCode is null || program.GenreCode == AnimeGenreCode);
        }
        return true;
    }

    // ProgramKeys: 通常の板で番組一致の判定に使うキー。
    // ProgramOnlyKeys: アニメ実況板のような番組専用の板で使うキー
    // （番組がアニメのとき（ジャンル不明を含む）だけ入る）。
    public (string[] ProgramKeys, string[] ProgramOnlyKeys) GetKeys(DateTimeOffset now)
    {
        ProgramKeys current, previous;
        lock (_lock)
        {
            current = _current;
            previous = now < _previousUntil ? _previous : ProgramKeys.Empty;
        }
        var programKeys = current.Keys.Concat(previous.Keys).Distinct().ToArray();
        var programOnlyKeys = current.Keys.Where(_ => current.AllowsProgramOnly)
            .Concat(previous.Keys.Where(_ => previous.AllowsProgramOnly))
            .Distinct()
            .ToArray();
        return (programKeys, programOnlyKeys);
    }

    // 番組名から作った照合キーと、番組専用の板（アニメ実況板など）を見てよいか
    private sealed record ProgramKeys(IReadOnlyList<string> Keys, bool AllowsProgramOnly)
    {
        public static readonly ProgramKeys Empty = new([], false);
    }
}

// 板の対応表と番組名から、チャンネルが追うスレを選ぶ。
// コメント配信（NichanUpstreamChannel）と、受信者がいないときの勢い表示（NichanMomentumService）で共用する。
public static class NichanThreadSelector
{
    public static async Task<IReadOnlyList<NichanSelectedThread>> SelectAsync(NichanBoardService boards,
        IReadOnlyList<NichanBoardEntry> entries, IReadOnlyList<string> programKeys,
        IReadOnlyList<string> programOnlyKeys, int maxThreads, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = new List<(NichanThreadInfo Thread, double Momentum, bool ProgramMatch)>();
        NichanException? lastError = null;
        var succeeded = 0;
        foreach (var entry in entries)
        {
            if (entry.ProgramOnly && programOnlyKeys.Count == 0)
                continue;
            IReadOnlyList<NichanThreadInfo> threads;
            try
            {
                threads = await boards.GetThreadsAsync(entry.Board, ct);
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
                var programMatch = NichanProgramMatcher.MatchesThread(thread.Title,
                    entry.ProgramOnly ? programOnlyKeys : programKeys);
                if (entry.ProgramOnly && !programMatch) continue;
                candidates.Add((thread, Momentum(thread, now), programMatch));
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
        var result = new List<NichanSelectedThread>();
        var bestMatch = ordered.FirstOrDefault(c => c.ProgramMatch);
        if (bestMatch.Thread != null)
            result.Add(new NichanSelectedThread(bestMatch.Thread, true));
        foreach (var c in ordered)
        {
            if (result.Count >= maxThreads) break;
            if (bestMatch.Thread != null && c.Thread.Address.Identity == bestMatch.Thread.Address.Identity) continue;
            result.Add(new NichanSelectedThread(c.Thread, c.ProgramMatch));
        }
        return result;
    }

    // 勢い（1 時間あたりのレス数）。直近の増え方がわかればそれを使う。
    // 全期間の平均だと、番組中に一気に伸びて今は止まったスレが上位に残り続けるため。
    // わからないとき（起動直後・観測期間の短い新スレ）は全期間の平均で代用する。
    // 立ったばかりのスレが過大にならないよう経過時間に下限を置く
    internal static double Momentum(NichanThreadInfo thread, DateTimeOffset now)
    {
        if (thread.RecentMomentum is { } recent)
            return recent;
        var ageHours = thread.Address.CreatedAt is { } created
            ? Math.Max(0.1, (now - created).TotalHours)
            : 24;
        return thread.ResCount / ageHours;
    }

    // NFKC 正規化 + 小文字化（"ＢＳ朝日" と "BS朝日" を同一視する）。
    // 空白は残す（"BS10 1150" が "BS11" に一致しないように）
    private static string NormalizeTitle(string title) =>
        title.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
}
