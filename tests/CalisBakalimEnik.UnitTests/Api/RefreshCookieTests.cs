using CalisBakalimEnik.Api.Features.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// The web client's refresh token lives in an HttpOnly cookie (B07).
/// </summary>
public class RefreshCookieTests
{
    private static readonly DateTimeOffset Expires = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static TokenResponse Tokens() =>
        new("access", Expires.AddMinutes(-30), "refresh-secret", Expires);

    private static HttpContext Web(bool optIn = true, string? cookie = null)
    {
        var http = new DefaultHttpContext();
        if (optIn) http.Request.Headers[RefreshCookie.OptInHeader] = "1";
        if (cookie is not null) http.Request.Headers.Cookie = $"{RefreshCookie.Name}={cookie}";
        return http;
    }

    private static string SetCookie(HttpContext http) =>
        http.Response.Headers.SetCookie.ToString();

    [Fact]
    public void A_web_client_gets_the_token_in_the_cookie_and_not_in_the_body()
    {
        var http = Web();

        var body = RefreshCookie.Issue(http, Tokens());

        body.RefreshToken.Should().BeNull("page scripts must never see it");
        body.AccessToken.Should().Be("access");

        var header = SetCookie(http).ToLowerInvariant();
        header.Should().Contain($"{RefreshCookie.Name}=refresh-secret");
        header.Should().Contain("httponly");
        header.Should().Contain("secure");
        header.Should().Contain("samesite=strict");
        header.Should().Contain($"path={RefreshCookie.Path}");
    }

    [Fact]
    public void Phones_and_old_cached_bundles_keep_the_token_in_the_body()
    {
        var http = Web(optIn: false);

        var body = RefreshCookie.Issue(http, Tokens());

        body.RefreshToken.Should().Be("refresh-secret");
        SetCookie(http).Should().BeEmpty();
    }

    [Fact]
    public void The_cookie_is_read_only_together_with_the_opt_in_header()
    {
        RefreshCookie.Read(Web(cookie: "abc")).Should().Be("abc");
        RefreshCookie.Read(Web(optIn: false, cookie: "abc"))
            .Should().BeNull("a forged form post cannot send a custom header");
        RefreshCookie.Read(Web()).Should().BeNull();
    }

    [Fact]
    public void Clearing_expires_it_on_the_same_path()
    {
        var http = Web();
        RefreshCookie.Clear(http);

        var header = SetCookie(http).ToLowerInvariant();
        header.Should().Contain($"{RefreshCookie.Name}=");
        header.Should().Contain("expires=thu, 01 jan 1970");
        header.Should().Contain($"path={RefreshCookie.Path}");
    }
}
