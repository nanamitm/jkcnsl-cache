using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;

namespace jkcnsl_cache;

public record LoginResult(
    string? UserSession = null,
    string? MfaTrustedDeviceToken = null,
    string? MfaToken = null,
    bool MfaRequired = false,
    string? Error = null
);

public static class NicovideoAuth
{
    private const string DeviceName = "jkcnsl-cache";
    private const string LoginUrl  = "https://account.nicovideo.jp/login?site=niconico";
    private const string LoginPost = "https://account.nicovideo.jp/login/redirector?site=niconico";

    private record MfaSession(HttpClient Http, CookieContainer Cookies, string MfaUri, DateTime Expires);
    private static readonly ConcurrentDictionary<string, MfaSession> _mfaSessions = new();

    public static async Task<LoginResult> LoginAsync(
        string email, string password, string? mfaTrustedDeviceToken, CancellationToken ct)
    {
        var cookies = new CookieContainer();

        // 端末信頼クッキーがあれば付与 → 2FA をスキップできる
        if (!string.IsNullOrEmpty(mfaTrustedDeviceToken))
        {
            foreach (var d in new[] { "account.nicovideo.jp", ".nicovideo.jp" })
                cookies.Add(new Cookie("mfa_trusted_device_token", mfaTrustedDeviceToken, "/", d));
        }

        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.Add("Accept-Language", "ja,en;q=0.9");

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, LoginPost)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["mail_tel"] = email,
                    ["password"] = password,
                }),
            };
            req.Headers.Add("Referer", LoginUrl);

            var resp = await http.SendAsync(req, ct);

            // 成功: x-niconico-id ヘッダーが返る
            if (resp.Headers.Contains("x-niconico-id"))
            {
                var session = ExtractUserSession(cookies);
                http.Dispose();
                return session != null
                    ? new LoginResult(UserSession: session)
                    : new LoginResult(Error: "user_session の取得に失敗しました");
            }

            // 2FA: MFA ページへリダイレクトされた
            var finalUrl = resp.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
            if (finalUrl.StartsWith("https://account.nicovideo.jp/mfa", StringComparison.Ordinal))
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var m = Regex.Match(body,
                    @"<form(?= )([^>]*? action=""(/mfa[^>""]*)""[^>]*)>",
                    RegexOptions.IgnoreCase);
                if (m.Success && Regex.IsMatch(m.Groups[1].Value, @" method=""[Pp][Oo][Ss][Tt]"""))
                {
                    var mfaUri = "https://account.nicovideo.jp" +
                                 HttpUtility.HtmlDecode(m.Groups[2].Value);
                    var token = Guid.NewGuid().ToString("N");
                    _mfaSessions[token] = new MfaSession(http, cookies, mfaUri,
                        DateTime.UtcNow.AddMinutes(5));
                    CleanupExpired();
                    return new LoginResult(MfaRequired: true, MfaToken: token);
                }
            }

            http.Dispose();
            return new LoginResult(Error: "ログインに失敗しました。メールアドレス・パスワードを確認してください");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            http.Dispose(); throw;
        }
        catch (Exception ex)
        {
            http.Dispose();
            return new LoginResult(Error: $"エラー: {ex.Message}");
        }
    }

    public static async Task<LoginResult> SubmitMfaAsync(
        string mfaToken, string otp, bool trustDevice, CancellationToken ct)
    {
        if (!_mfaSessions.TryRemove(mfaToken, out var session))
            return new LoginResult(Error: "セッションが見つかりません（タイムアウト: 5分）");

        if (DateTime.UtcNow > session.Expires)
        {
            session.Http.Dispose();
            return new LoginResult(Error: "セッションがタイムアウトしました");
        }

        using (session.Http)
        {
            try
            {
                var formData = new List<KeyValuePair<string, string>>
                {
                    new("otp", otp.Trim()),
                    new("loginBtn", "ログイン"),
                    new("device_name", DeviceName),
                };
                if (trustDevice)
                    formData.Add(new("is_mfa_trusted_device", "true"));

                var req = new HttpRequestMessage(HttpMethod.Post, session.MfaUri)
                {
                    Content = new FormUrlEncodedContent(formData),
                };
                req.Headers.Add("Referer", session.MfaUri);

                var resp = await session.Http.SendAsync(req, ct);

                if (!resp.Headers.Contains("x-niconico-id"))
                    return new LoginResult(Error: "ワンタイムパスワードが正しくありません");

                var userSession = ExtractUserSession(session.Cookies);
                if (userSession == null)
                    return new LoginResult(Error: "user_session の取得に失敗しました");

                string? trustedToken = null;
                if (trustDevice)
                {
                    foreach (var d in new[] { "nicovideo.jp", "account.nicovideo.jp" })
                    {
                        trustedToken = session.Cookies
                            .GetCookies(new Uri($"https://{d}"))["mfa_trusted_device_token"]?.Value;
                        if (!string.IsNullOrEmpty(trustedToken)) break;
                    }
                }

                return new LoginResult(UserSession: userSession, MfaTrustedDeviceToken: trustedToken);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return new LoginResult(Error: $"エラー: {ex.Message}"); }
        }
    }

    private static string? ExtractUserSession(CookieContainer cookies)
    {
        foreach (var d in new[] { "nicovideo.jp", "account.nicovideo.jp" })
        {
            var v = cookies.GetCookies(new Uri($"https://{d}"))["user_session"]?.Value;
            if (!string.IsNullOrEmpty(v) && v != "deleted") return v;
        }
        return null;
    }

    private static void CleanupExpired()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _mfaSessions)
            if (now > kv.Value.Expires && _mfaSessions.TryRemove(kv.Key, out var s))
                s.Http.Dispose();
    }
}
