namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// The response headers docs/SECURITY.md §8 promises — which, until this
/// existed, nothing set.
/// </summary>
/// <remarks>
/// <para>Most of these matter less on a JSON API than on a page, and it would
/// be easy to argue them away one at a time. They are here because they cost
/// one dictionary write each, because a header that is absent is indisputably
/// absent while "this endpoint will never return HTML" is a claim about the
/// future, and because the security document said they were set.</para>
///
/// <para><b>HSTS is written by hand rather than through
/// <c>UseHsts()</c>.</b> That helper only emits the header on a request it
/// believes is HTTPS, and every request here arrives over plain HTTP from the
/// Cloudflare Tunnel on localhost — so <c>UseHsts()</c> would have emitted
/// nothing, forever, while looking like it worked. The browser's connection is
/// to Cloudflare and it is TLS; this header travels back through it.</para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Hsts = "max-age=31536000; includeSubDomains";

    /// <summary>
    /// This API answers JSON and nothing else, so the honest policy is that a
    /// browser should load nothing at all on its behalf. The Flutter web bundle
    /// is served by something else and needs its own, looser policy — CanvasKit
    /// wants <c>wasm-unsafe-eval</c> — which belongs wherever that bundle is
    /// hosted, not here.
    /// </summary>
    private const string Csp = "default-src 'none'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Content-Security-Policy"] = Csp;
        headers["Strict-Transport-Security"] = Hsts;

        return next(context);
    }
}
