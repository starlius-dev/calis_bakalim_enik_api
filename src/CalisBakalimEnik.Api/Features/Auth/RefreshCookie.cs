namespace CalisBakalimEnik.Api.Features.Auth;

/// <summary>
/// The web client's refresh token, in a cookie its JavaScript cannot read (B07).
/// </summary>
/// <remarks>
/// <para>A refresh token in browser storage is one XSS away from a session that
/// lasts for weeks. In an HttpOnly cookie the page can use it (the browser
/// sends it to /auth) but never see it, so an injected script can at most act
/// while the page is open, and cannot carry the session off.</para>
///
/// <para>Opt-in per request: a client that sends <c>X-Refresh-Cookie: 1</c>
/// gets the cookie and no token in the body; anyone else (the phone apps,
/// and a web bundle from before this change still cached in a browser) keeps
/// getting the token in the body as before. Without that, an old cached
/// bundle would receive a response without the token it expects and sign its
/// user out.</para>
///
/// <para>SameSite=Strict, Secure, path limited to the auth endpoints. The
/// web app and the API are different origins but the same site
/// (starlius.com), so the browser still sends it, while a request started
/// from any other site never carries it; CORS refuses other origins on top.</para>
/// </remarks>
public static class RefreshCookie
{
    public const string Name = "enik_rt";
    public const string OptInHeader = "X-Refresh-Cookie";
    public const string Path = "/api/v1/auth";

    /// <summary>Whether this client keeps its refresh token in the cookie.</summary>
    public static bool Wanted(HttpContext http) =>
        http.Request.Headers[OptInHeader] == "1";

    /// <summary>
    /// The cookie, but only on a request that also carries the opt-in header.
    /// A custom header cannot be sent cross-origin without a CORS preflight,
    /// which the API refuses for unknown origins, so a forged form post (even
    /// from another starlius.com subdomain, which SameSite alone would let
    /// through) never gets to use the cookie.
    /// </summary>
    public static string? Read(HttpContext http) =>
        Wanted(http)
        && http.Request.Cookies.TryGetValue(Name, out var value)
        && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public static void Write(HttpContext http, string token, DateTimeOffset expires) =>
        http.Response.Cookies.Append(Name, token, Options(expires));

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Delete(Name, Options(DateTimeOffset.UnixEpoch));

    private static CookieOptions Options(DateTimeOffset expires) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expires,
        IsEssential = true,
    };

    /// <summary>
    /// The tokens as this client should get them: for a cookie client the
    /// refresh token goes into the cookie and is left out of the body.
    /// </summary>
    public static TokenResponse Issue(HttpContext http, TokenResponse tokens)
    {
        if (!Wanted(http)) return tokens;

        Write(http, tokens.RefreshToken!, tokens.RefreshExpiresAt);
        return tokens with { RefreshToken = null };
    }
}
