using System.Security.Claims;
using CalisBakalimEnik.Infrastructure.Identity;

namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// The blanket per-user budget for everything behind a login.
/// </summary>
/// <remarks>
/// Middleware rather than a per-endpoint filter, because this one is a rule
/// about the whole authenticated surface: as a filter it would have to be
/// remembered on every new group, and the one that got forgotten would be the
/// unmetered one.
///
/// It runs AFTER authentication, so the partition is the real user, and it
/// skips anonymous requests — those are covered by the per-endpoint budgets on
/// login, register, refresh and forgot-password, which are partitioned by IP.
///
/// 300 a minute is generous on purpose. It exists to stop a runaway client or
/// a scraped token, not to ration ordinary use: a real session opening several
/// screens fires a dozen requests in a second.
/// </remarks>
public sealed class AuthenticatedRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, RateLimitGuard guard)
    {
        if (http.User.Identity?.IsAuthenticated != true)
        {
            await next(http);
            return;
        }

        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? http.User.FindFirstValue("sub");

        if (userId is null)
        {
            await next(http);
            return;
        }

        var verdict = await guard.CheckAsync(
            RateLimitGuard.Policies.Authenticated, userId, http.RequestAborted);

        http.Response.Headers["X-RateLimit-Limit"] = verdict.Limit.ToString();
        http.Response.Headers["X-RateLimit-Remaining"] = verdict.Remaining.ToString();

        if (verdict.Allowed)
        {
            await next(http);
            return;
        }

        if (verdict.RetryAfter is { } retry)
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
        }

        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.ContentType = "application/problem+json";

        await http.Response.WriteAsJsonAsync(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.29",
            title = "Çok fazla istek",
            status = StatusCodes.Status429TooManyRequests,
            detail = "Çok hızlı denedin. Biraz bekleyip tekrar dene.",
        }, http.RequestAborted);
    }
}
