using System.Security.Claims;
using CalisBakalimEnik.Infrastructure.Identity;

namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// Applies a <see cref="RateLimitPolicy"/> to an endpoint.
/// </summary>
/// <remarks>
/// An endpoint filter rather than middleware: the budget belongs to the
/// endpoint, and declaring it next to the route means a new endpoint cannot
/// silently inherit — or miss — someone else's limit.
///
/// The response is a 429 with <c>Retry-After</c> and the <c>X-RateLimit-*</c>
/// headers. Without them a client has no way to back off except by guessing,
/// and guessing is how a retry loop becomes the attack.
/// </remarks>
public sealed class RateLimitFilter(RateLimitGuard guard, RateLimitPolicy policy)
    : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var partition = policy.PerUser ? UserPartition(http) : ClientIp(http);

        var verdict = await guard.CheckAsync(policy, partition, http.RequestAborted);

        http.Response.Headers["X-RateLimit-Limit"] = verdict.Limit.ToString();
        http.Response.Headers["X-RateLimit-Remaining"] = verdict.Remaining.ToString();

        if (verdict.Allowed) return await next(context);

        if (verdict.RetryAfter is { } retry)
        {
            http.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
        }

        return Results.Problem(
            title: "Çok fazla istek",
            detail: "Çok hızlı denedin. Biraz bekleyip tekrar dene.",
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    /// The user id, falling back to the IP for a caller with no token yet.
    /// Returning null here would exempt anonymous traffic from a per-user
    /// policy entirely.
    /// </summary>
    private static string? UserPartition(HttpContext http)
    {
        var id = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
                 ?? http.User.FindFirstValue("sub");

        return id ?? ClientIp(http);
    }

    /// <summary>
    /// The REAL client. Behind Cloudflare every request carries the proxy's
    /// address, so limiting on that would put every user in one bucket and the
    /// first busy minute would lock out the world. See docs/SECURITY.md §1.
    /// </summary>
    private static string? ClientIp(HttpContext http) =>
        http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf)
        && !string.IsNullOrWhiteSpace(cf)
            ? cf.ToString()
            : http.Connection.RemoteIpAddress?.ToString();
}

public static class RateLimitExtensions
{
    /// <summary>Applies a budget to one endpoint.</summary>
    public static TBuilder RateLimit<TBuilder>(
        this TBuilder builder, RateLimitPolicy policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((context, next) =>
        {
            var guard = context.ApplicationServices
                .GetRequiredService<RateLimitGuard>();

            var filter = new RateLimitFilter(guard, policy);

            return invocation => filter.InvokeAsync(invocation, next);
        });

        return builder;
    }
}
