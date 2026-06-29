namespace jkcnsl_cache;

public sealed class NicovideoSessionStore
{
    private string? _cookieHeader;

    public string? GetCookieHeader() => Volatile.Read(ref _cookieHeader);

    public void SetUserSession(string? userSessionOrCookie)
    {
        Volatile.Write(ref _cookieHeader, NormalizeCookie(userSessionOrCookie));
    }

    private static string? NormalizeCookie(string? userSessionOrCookie)
    {
        if (string.IsNullOrWhiteSpace(userSessionOrCookie)) return null;

        var value = userSessionOrCookie.Trim();
        return value.StartsWith("user_session=", StringComparison.OrdinalIgnoreCase)
            ? value
            : $"user_session={value}";
    }
}
