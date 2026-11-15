namespace jkcnsl_cache.Nichan;

// 板 1 つ分の割り当て。Keywords が空なら板の全スレが対象。
// Excludes はキーワードに一致しても除外する語（"NHK BS" と "NHK BSプレミアム4K" の区別など）。
public sealed record NichanBoardEntry(Uri Board, IReadOnlyList<string> Keywords, IReadOnlyList<string> Excludes);

// jk ID → 5ch 実況板・スレタイキーワードの対応表。
// 既定値は TVTComment（noriokun4649/TVTComment, MIT）の Data/2chthreads.txt を元に、
// 2026-11 時点のスレタイに合わせて調整した。appsettings.json の CacheServer:Nichan:Boards で上書きできる。
public sealed class NichanBoardCatalog
{
    private static readonly Dictionary<string, NichanBoardEntry[]> Defaults = new(StringComparer.Ordinal)
    {
        ["jk1"]   = [B("https://nhk2.5ch.io/livenhk/")],
        ["jk2"]   = [B("https://nhk2.5ch.io/liveetv/")],
        ["jk4"]   = [B("https://sora.5ch.io/liventv/")],
        ["jk5"]   = [B("https://sora.5ch.io/liveanb/")],
        ["jk6"]   = [B("https://sora.5ch.io/livetbs/")],
        ["jk7"]   = [B("https://sora.5ch.io/livetx/")],
        ["jk8"]   = [B("https://sora.5ch.io/livecx/")],
        ["jk9"]   = [B("https://fate.5ch.io/livemx/"), B("https://sora.5ch.io/weekly/", ["MX"])],
        ["jk10"]  = [B("https://sora.5ch.io/weekly/", ["テレ玉"])],
        ["jk11"]  = [B("https://sora.5ch.io/weekly/", ["tvk"])],
        ["jk12"]  = [B("https://sora.5ch.io/weekly/", ["チバ"])],
        ["jk13"]  = [B("https://sora.5ch.io/livewkwest/", ["関西ローカル"])],
        ["jk14"]  = [B("https://sora.5ch.io/livewkwest/", ["関西ローカル"])],
        ["jk101"] = [B("https://nhk2.5ch.io/livebs/", ["NHK BS"], ["プレミアム", "4K", "8K"])],
        ["jk103"] = [B("https://nhk2.5ch.io/livebs/", ["プレミアム4K", "BSP4K"])],
        ["jk104"] = [B("https://nhk2.5ch.io/livebs/", ["8K"])],
        ["jk141"] = [B("https://tanuki.5ch.io/livebs2/", ["BS日テレ"])],
        ["jk151"] = [B("https://tanuki.5ch.io/livebs2/", ["BS朝日"])],
        ["jk161"] = [B("https://tanuki.5ch.io/livebs2/", ["BS-TBS", "BSTBS"])],
        ["jk171"] = [B("https://tanuki.5ch.io/livebs2/", ["BSテレ東", "BSジャパン"])],
        ["jk181"] = [B("https://tanuki.5ch.io/livebs2/", ["BSフジ"])],
        ["jk191"] = [B("https://sora.5ch.io/livewowow/", ["WOWOWプライム"])],
        ["jk192"] = [B("https://sora.5ch.io/livewowow/", ["WOWOWライブ"])],
        ["jk193"] = [B("https://sora.5ch.io/livewowow/", ["WOWOWシネマ"])],
        ["jk200"] = [B("https://tanuki.5ch.io/livebs2/", ["BS10"], ["スター"])],
        ["jk201"] = [B("https://sora.5ch.io/livewowow/", ["スターチャンネル", "スタチャン"])],
        ["jk211"] = [B("https://tanuki.5ch.io/livebs2/", ["BS11"])],
        ["jk222"] = [B("https://tanuki.5ch.io/livebs2/", ["BS12", "TwellV"])],
        ["jk231"] = [B("https://tanuki.5ch.io/livebs2/", ["放送大学"])],
        ["jk232"] = [B("https://tanuki.5ch.io/livebs2/", ["放送大学"])],
        ["jk234"] = [B("https://sora.5ch.io/liveskyp/", ["グリーンチャンネル"])],
        ["jk236"] = [B("https://sora.5ch.io/liveskyp/", ["アニマックス"])],
        ["jk252"] = [B("https://sora.5ch.io/livewowow/", ["WOWOWプラス", "WOWOW PLUS"])],
        ["jk255"] = [B("https://sora.5ch.io/liveskyp/", ["日本映画専門"])],
        ["jk260"] = [B("https://tanuki.5ch.io/livebs2/", ["JCOMBS", "J:COM BS", "JCOM BS"])],
        ["jk265"] = [B("https://tanuki.5ch.io/livebs2/", ["BSよしもと"])],
        ["jk333"] = [B("https://sora.5ch.io/liveskyp/", ["AT-X"])],
    };

    private readonly Dictionary<string, NichanBoardEntry[]> _entries;

    public NichanBoardCatalog(IConfiguration config, ILogger<NichanBoardCatalog> logger)
    {
        _entries = new Dictionary<string, NichanBoardEntry[]>(Defaults, StringComparer.Ordinal);
        foreach (var channel in config.GetSection("CacheServer:Nichan:Boards").GetChildren())
        {
            var list = new List<NichanBoardEntry>();
            foreach (var item in channel.GetChildren())
            {
                var board = item.GetValue<string>("Board");
                if (string.IsNullOrWhiteSpace(board) || !Uri.TryCreate(board, UriKind.Absolute, out var uri))
                {
                    logger.LogWarning("CacheServer:Nichan:Boards:{Channel} の Board が不正です: {Board}", channel.Key, board);
                    continue;
                }
                try
                {
                    list.Add(new NichanBoardEntry(NichanThreadAddress.NormalizeBoard(uri),
                        ReadStrings(item.GetSection("Keywords")),
                        ReadStrings(item.GetSection("Excludes"))));
                }
                catch (FormatException ex)
                {
                    logger.LogWarning("CacheServer:Nichan:Boards:{Channel}: {Message}", channel.Key, ex.Message);
                }
            }
            _entries[channel.Key] = list.ToArray();
        }
    }

    // jk104n のようなソースキーでも、jk104 のような表示チャンネル ID でも引ける
    public IReadOnlyList<NichanBoardEntry> Get(string channel)
    {
        if (_entries.TryGetValue(channel, out var entries))
            return entries;
        if (channel.EndsWith('n') && _entries.TryGetValue(channel[..^1], out entries))
            return entries;
        return [];
    }

    private static string[] ReadStrings(IConfigurationSection section)
    {
        var values = section.GetChildren().Select(c => c.Value).OfType<string>().ToList();
        if (values.Count == 0 && !string.IsNullOrWhiteSpace(section.Value))
            values.Add(section.Value);
        return values.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray();
    }

    private static NichanBoardEntry B(string board, string[]? keywords = null, string[]? excludes = null) =>
        new(new Uri(board), keywords ?? [], excludes ?? []);
}
