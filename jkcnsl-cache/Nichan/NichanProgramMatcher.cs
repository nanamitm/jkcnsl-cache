using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace jkcnsl_cache.Nichan;

// 番組表の番組名と 5ch のスレタイを照合する。
// 番組表の番組名には「[字][再]」「＃6」「第6話」「（49-54）」「「サブタイトル」」などが付くため、
// それらを落とした主タイトルを「キー」とし、スレタイ（空白・記号を除いたもの）がキーを含めば一致とみなす。
//   "[再]バジリスク 〜甲賀忍法帖〜 ＃６「降涙恋慕」" → キー "バジリスク甲賀忍法帖", "バジリスク"
//   "アニメ「ふしぎの海のナディア」４Ｋリマスター版（３２）" → キー "ふしぎの海のナディア"
public static partial class NichanProgramMatcher
{
    // 番組名がこれらだけのときは、どのスレにも当てはまりうるのでキーにしない
    private static readonly HashSet<string> GenericKeys = new(StringComparer.Ordinal)
    {
        "ニュース", "ショッピング", "テレビショッピング", "ショッピング情報", "通販", "オープニング", "エンディング",
        "天気", "天気予報", "気象情報", "休止", "放送休止", "定時ニュース", "インフォメーション", "アニメ", "ドラマ",
        "映画", "特撮", "時代劇", "韓国ドラマ", "中国ドラマ", "スポーツ", "音楽", "再放送",
    };

    public static IReadOnlyList<string> BuildKeys(string? programTitle)
    {
        if (string.IsNullOrWhiteSpace(programTitle))
            return [];
        var t = programTitle.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);

        // [字] [再] 【解】 <お昼のサスペンス> (49-54) などの括弧書きを落とす
        t = BracketRegex().Replace(t, " ");

        // 「中国◇」「韓国◇」のような分類の前置きを落とす
        t = CategoryPrefixRegex().Replace(t, "");

        // アニメ「作品名」 のように、ジャンル名 + 鉤括弧の場合は鉤括弧の中身が主タイトル
        var quoted = GenreQuotedRegex().Match(t);
        if (quoted.Success)
            t = quoted.Groups["title"].Value;

        // 話数・サブタイトル・区切り記号以降を落とす
        t = EpisodeSplitRegex().Split(t)[0];

        var keys = new List<string>();
        AddKey(keys, t);
        // 先頭の語（"バジリスク 〜甲賀忍法帖〜" の "バジリスク"）も、スレタイで副題が省かれる場合に備えてキーにする
        var first = WordSplitRegex().Split(t.Trim()).FirstOrDefault(s => s.Length > 0);
        if (first != null)
            AddKey(keys, first);
        return keys;
    }

    // 照合用にスレタイ・キーを正規化する（NFKC・小文字化・空白と一部記号の除去）
    public static string Compact(string text)
    {
        var t = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        return CompactRemoveRegex().Replace(t, "");
    }

    public static bool Matches(string compactThreadTitle, IReadOnlyList<string> keys) =>
        keys.Any(key => compactThreadTitle.Contains(key, StringComparison.Ordinal));

    // スレタイが番組名に一致するか。キーがスレタイに含まれるか、逆にスレタイの先頭の語でキーが始まれば一致とする。
    // 後者は、番組表で番組名と副題が区切りなしにつながっている場合のため:
    //   番組名 "メシドラディーン・フジオカが育った…" / スレタイ "メシドラ　 ゲスト：ディーン・フジオカ"
    public static bool MatchesThread(string threadTitle, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
            return false;
        if (Matches(Compact(threadTitle), keys))
            return true;
        var lead = ThreadLeadKey(threadTitle);
        if (lead == null)
            return false;
        // 番組名の途中に含まれる場合（番組名 "開運!なんでも鑑定団" / スレタイ "なんでも鑑定団"）は、
        // 誤一致を避けるため全角を含む 5 文字以上の語に限る
        var allowContains = lead.Length >= 5 && !lead.All(char.IsAscii);
        return keys.Any(key => allowContains
            ? key.Contains(lead, StringComparison.Ordinal)
            : key.StartsWith(lead, StringComparison.Ordinal));
    }

    // スレタイの先頭の語（括弧書き・スレ番号・副題を落としたもの）。短すぎる・一般的すぎる語なら null
    public static string? ThreadLeadKey(string threadTitle)
    {
        var t = threadTitle.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        t = BracketRegex().Replace(t, " ");
        t = EpisodeSplitRegex().Split(t)[0];
        var first = WordSplitRegex().Split(t.Trim()).FirstOrDefault(s => s.Length > 0);
        if (first == null)
            return null;
        var key = Compact(first);
        return IsUsableKey(key) ? key : null;
    }

    private static void AddKey(List<string> keys, string raw)
    {
        var key = Compact(raw);
        if (IsUsableKey(key) && !keys.Contains(key))
            keys.Add(key);
    }

    private static bool IsUsableKey(string key)
    {
        // 全角を含まないキーは短いと誤一致しやすいので長めを要求する
        var minLength = key.All(char.IsAscii) ? 4 : 3;
        return key.Length >= minLength && !GenericKeys.Contains(key) &&
               !key.Contains("ショッピング", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\[[^\]]*\]|【[^】]*】|<[^>]*>|〈[^〉]*〉|《[^》]*》|\([^)]*\)")]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"^\s*(?:アニメ|ドラマ|映画|特撮|時代劇|韓国ドラマ|中国ドラマ|連続アニメ)?\s*[「『](?<title>[^」』]+)[」』]")]
    private static partial Regex GenreQuotedRegex();

    [GeneratedRegex(@"^\s*[^◇◆\s]{1,6}[◇◆]")]
    private static partial Regex CategoryPrefixRegex();

    [GeneratedRegex(@"#|第\s*\d|▼|▽|◆|★|☆|「|\s-\s|\s/\s")]
    private static partial Regex EpisodeSplitRegex();

    [GeneratedRegex(@"[\s~〜]+")]
    private static partial Regex WordSplitRegex();

    [GeneratedRegex(@"[\s・!?~〜\-‐–—:;,.、。'""“”’]")]
    private static partial Regex CompactRemoveRegex();
}
