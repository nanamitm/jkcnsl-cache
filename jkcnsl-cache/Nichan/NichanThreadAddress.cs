using System.Text.RegularExpressions;

namespace jkcnsl_cache.Nichan;

// 板 URL（https://{server}.5ch.io/{board}/）とスレッドキーの組。
// URL の正規化方針は TVTComment（noriokun4649/TVTComment, MIT）の Nichan.ThreadAddress に倣う。
public sealed partial record NichanThreadAddress(Uri BoardUri, string Key)
{
    public string Board => BoardUri.AbsolutePath.Trim('/');
    public Uri DatUri => new(BoardUri, $"dat/{Key}.dat");
    public Uri ThreadUri => new(BoardUri, $"/test/read.cgi/{Board}/{Key}/");
    public string Identity => $"{BoardUri.Host}/{Board}/{Key}";

    // スレ立て時刻（スレッドキーは UNIX 時刻）
    public DateTimeOffset? CreatedAt =>
        long.TryParse(Key, out var unix) && unix is > 900_000_000 and < 9_000_000_000
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    // 運営告知スレ（924 で始まる 10 桁）
    public bool IsAnnouncement => Key.Length == 10 && Key.StartsWith("924", StringComparison.Ordinal);

    public static bool Is5chHost(string host) =>
        new[] { "5ch.io", "5ch.net", "2ch.net" }.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public static Uri NormalizeBoard(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new FormatException("板の URL は http(s) で指定してください");
        var board = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrEmpty(board) || !BoardNameRegex().IsMatch(board))
            throw new FormatException($"板の URL が不正です: {uri}");
        var builder = new UriBuilder(uri) { Path = "/" + board + "/", Query = "", Fragment = "" };
        if (Is5chHost(uri.Host))
        {
            builder.Host = HostSuffixRegex().Replace(uri.Host, "5ch.io");
            builder.Scheme = Uri.UriSchemeHttps;
            builder.Port = -1;
        }
        return builder.Uri;
    }

    public static bool TryParseThreadUrl(string url, out NichanThreadAddress? address)
    {
        address = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var match = ReadCgiRegex().Match(uri.AbsolutePath);
        if (!match.Success)
            return false;
        try
        {
            var board = NormalizeBoard(new Uri(uri, "/" + match.Groups["board"].Value + "/"));
            address = new NichanThreadAddress(board, match.Groups["key"].Value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-zA-Z0-9_]+$")]
    private static partial Regex BoardNameRegex();

    [GeneratedRegex(@"(?:5ch|2ch)\.(?:net|io)$", RegexOptions.IgnoreCase)]
    private static partial Regex HostSuffixRegex();

    [GeneratedRegex(@"^/(?:test/)?read\.cgi/(?:c/)?(?<board>[a-zA-Z0-9_]+)/(?<key>[0-9]{9,12})(?:/|$)")]
    private static partial Regex ReadCgiRegex();
}
