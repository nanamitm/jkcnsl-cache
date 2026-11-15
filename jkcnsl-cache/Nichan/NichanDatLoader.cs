using System.Net;
using System.Net.Http.Headers;

namespace jkcnsl_cache.Nichan;

public sealed class NichanThreadGoneException : NichanException
{
    public NichanThreadGoneException(HttpStatusCode code)
        : base($"スレッドが取得できません（HTTP {(int)code}）") { }
}

// 1 スレッド分の dat を差分取得する。
// 方式は TVTComment（noriokun4649/TVTComment, MIT）の Nichan.DatThreadLoader に倣う:
// 2 回目以降は「既取得長 - 1」からの Range 要求とし、先頭 1 バイトが改行であることで差分の整合性を確認する。
public sealed class NichanDatLoader
{
    private byte[] _data = [];
    private string? _lastModified;
    private int _lineCount;

    public NichanThreadAddress Address { get; private set; }
    public string? Title { get; private set; }
    public int ResCount => _lineCount;

    public NichanDatLoader(NichanThreadAddress address)
    {
        Address = address;
    }

    // 新しく増えたレスを返す（初回はすべて）
    public async Task<IReadOnlyList<NichanRes>> UpdateAsync(NichanHttpClient http, CancellationToken ct)
    {
        var full = _data.Length == 0;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Address.DatUri);
            if (!full)
            {
                request.Headers.Range = new RangeHeaderValue(_data.Length - 1, null);
                if (_lastModified != null)
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", _lastModified);
            }

            using var response = await http.SendAsync(request, ct);
            var code = response.StatusCode;
            if (code == HttpStatusCode.NotModified && !full)
                return [];
            if (code is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                throw new NichanThreadGoneException(code);
            if (code == HttpStatusCode.RequestedRangeNotSatisfiable && !full)
            {
                // サイズが縮んだ（あぼーん等で dat が書き換えられた）ので全体を取り直す
                full = true;
                continue;
            }
            if (code is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new NichanException($"dat の取得に失敗しました（HTTP {(int)code}）");
            if (response.Content.Headers.ContentEncoding.Count > 0)
                throw new NichanException("圧縮された dat には対応していません");

            var body = await response.Content.ReadAsByteArrayAsync(ct);
            byte[] candidate;
            if (code == HttpStatusCode.OK)
            {
                candidate = body;
            }
            else
            {
                var range = response.Content.Headers.ContentRange;
                var valid = !full && body.Length > 0 && body[0] == (byte)'\n' &&
                            range?.Unit == "bytes" && range.From == _data.Length - 1;
                if (!valid)
                {
                    full = true;
                    continue;
                }
                candidate = new byte[_data.Length + body.Length - 1];
                Buffer.BlockCopy(_data, 0, candidate, 0, _data.Length);
                Buffer.BlockCopy(body, 1, candidate, _data.Length, body.Length - 1);
            }

            // 末尾が行の途中なら、そこは次回取り直す
            var end = Array.LastIndexOf(candidate, (byte)'\n');
            if (end < 0)
                return [];
            if (end + 1 < candidate.Length)
                candidate = candidate[..(end + 1)];

            _lastModified = response.Content.Headers.LastModified?.ToString("r");
            var previousLines = code == HttpStatusCode.OK ? 0 : _lineCount;
            var knownLines = _lineCount;
            var newBytes = code == HttpStatusCode.OK
                ? candidate
                : candidate[_data.Length..];
            _data = candidate;
            return ParseNewLines(newBytes, previousLines, knownLines);
        }
        throw new NichanException("dat の差分取得に失敗しました");
    }

    // startLine: newBytes の先頭行の直前までの行数。knownLines 以下の番号のレスは返さない。
    private List<NichanRes> ParseNewLines(byte[] newBytes, int startLine, int knownLines)
    {
        var text = NichanDatParser.ShiftJis.GetString(newBytes);
        var result = new List<NichanRes>();
        var number = startLine;
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;
            number++;
            var res = NichanDatParser.ParseLine(number, line, out var title);
            if (number == 1 && title != null)
                Title = title;
            if (number > knownLines)
                result.Add(res);
        }
        _lineCount = number;
        return result;
    }
}
