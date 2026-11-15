using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace jkcnsl_cache.Nichan;

public sealed record NichanRes(int Number, DateTimeOffset? PostedAt, string? Id, string BodyHtml);

public sealed record NichanSubjectEntry(string Key, string Title, int ResCount);

// dat（名前<>メール<>日付 ID<>本文<>スレタイ）と subject.txt の解析。
public static partial class NichanDatParser
{
    private static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    public static Encoding ShiftJis { get; } = CreateShiftJis();

    private static Encoding CreateShiftJis()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    // 1 行を解析する。壊れた行（あぼーん等）は PostedAt=null / 本文空で返す。
    public static NichanRes ParseLine(int number, string line, out string? title)
    {
        title = null;
        var fields = line.Split("<>");
        if (fields.Length < 4)
            return new NichanRes(number, null, null, "");
        if (fields.Length >= 5 && !string.IsNullOrWhiteSpace(fields[4]))
            title = WebUtility.HtmlDecode(fields[4].Trim());
        var (postedAt, id) = ParseDateField(fields[2]);
        return new NichanRes(number, postedAt, id, fields[3]);
    }

    public static (DateTimeOffset? PostedAt, string? Id) ParseDateField(string field)
    {
        DateTimeOffset? postedAt = null;
        var m = DateRegex().Match(field);
        if (m.Success)
        {
            try
            {
                var fraction = m.Groups["f"].Success ? m.Groups["f"].Value : "";
                var ms = fraction.Length == 0 ? 0
                    : int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                postedAt = new DateTimeOffset(
                    int.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["mo"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["mi"].Value, CultureInfo.InvariantCulture),
                    int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture),
                    ms, Jst);
            }
            catch (ArgumentOutOfRangeException) { }
        }
        var idMatch = IdRegex().Match(field);
        return (postedAt, idMatch.Success ? idMatch.Groups[1].Value : null);
    }

    // 本文 HTML をコメント 1 行のテキストにする。行数が maxLines を超える（AA 等）場合は null。
    public static string? BodyToComment(string bodyHtml, int maxLines, int maxLength)
    {
        var lines = BrRegex().Split(bodyHtml)
            .Select(StripAndDecode)
            .Where(l => l.Length > 0)
            .ToArray();
        if (lines.Length == 0 || lines.Length > maxLines)
            return null;
        var text = SpaceRegex().Replace(string.Join(' ', lines), " ").Trim();
        if (text.Length == 0)
            return null;
        if (text.Length > maxLength)
            text = text[..(maxLength - 1)] + "…";
        return text;
    }

    private static string StripAndDecode(string html)
    {
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, ""));
        // 制御文字はコメントに残さない
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsControl(c) ? ' ' : c);
        return sb.ToString().Trim();
    }

    // subject.txt の 1 行: "1794698457.dat<>スレタイ (92)"
    public static NichanSubjectEntry? ParseSubjectLine(string line)
    {
        var m = SubjectRegex().Match(line);
        if (!m.Success)
            return null;
        var title = WebUtility.HtmlDecode(m.Groups["title"].Value).Trim();
        return new NichanSubjectEntry(m.Groups["key"].Value, title,
            int.Parse(m.Groups["count"].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"(?<y>\d{4})/(?<mo>\d{1,2})/(?<d>\d{1,2})(?:\([^)]*\))?\s*(?<h>\d{1,2}):(?<mi>\d{2}):(?<s>\d{2})(?:\.(?<f>\d+))?")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"ID:([^\s<]+)")]
    private static partial Regex IdRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"^(?<key>\d{9,12})\.dat<>(?<title>.*)\s*\((?<count>\d+)\)\s*$")]
    private static partial Regex SubjectRegex();
}
