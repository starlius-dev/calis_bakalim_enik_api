using CalisBakalimEnik.Application.Common.Interfaces;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>What a limiter decided, and what to tell the caller.</summary>
public readonly record struct RateLimitVerdict(
    bool Allowed,
    int Limit,
    long Used,
    TimeSpan? RetryAfter)
{
    public int Remaining => (int)Math.Max(0, Limit - Used);
}

/// <summary>One endpoint's budget.</summary>
/// <param name="Name">Used in the Redis key, so it must be stable.</param>
/// <param name="Limit">Requests allowed per window.</param>
/// <param name="Window">How long the window lasts.</param>
/// <param name="PerUser">
/// Partition by the authenticated user rather than the IP. Anything behind a
/// login should use this: several people on one office NAT share an address,
/// and an IP budget would let one of them exhaust everyone else's.
/// </param>
public readonly record struct RateLimitPolicy(
    string Name,
    int Limit,
    TimeSpan Window,
    bool PerUser = false);

/// <summary>
/// Endpoint rate limits — layer 3 of docs/SECURITY.md §5.
/// </summary>
/// <remarks>
/// A fixed window in Redis rather than ASP.NET Core's in-memory
/// <c>RateLimiter</c>, because the limits have to survive a restart and hold
/// across instances. An in-process limiter resets every deploy, which is the
/// moment an attacker would most like it to.
///
/// It is <b>fail-open</b>: if Redis is unreachable the request is allowed. This
/// is deliberate. These limits are abuse control, not authorisation — the real
/// account protection is <see cref="BruteForceGuard"/> and the auth checks
/// themselves — and failing closed would turn a cache outage into a total
/// outage.
/// </remarks>
public sealed class RateLimitGuard(ICacheStore cache)
{
    /// <summary>The budgets from docs/SECURITY.md §5, layer 3.</summary>
    public static class Policies
    {
        public static readonly RateLimitPolicy Login =
            new("login", 10, TimeSpan.FromMinutes(1));

        public static readonly RateLimitPolicy Refresh =
            new("refresh", 30, TimeSpan.FromMinutes(1));

        public static readonly RateLimitPolicy Register =
            new("register", 3, TimeSpan.FromHours(1));

        public static readonly RateLimitPolicy ForgotPassword =
            new("forgot", 3, TimeSpan.FromHours(1));

        /// <summary>
        /// The blanket budget for everything behind a login. Generous on
        /// purpose: it is there to stop a runaway client or a scraped token,
        /// not to ration ordinary use, and a real session opening several
        /// screens fires a dozen requests in a second.
        /// </summary>
        public static readonly RateLimitPolicy Authenticated =
            new("authed", 300, TimeSpan.FromMinutes(1), PerUser: true);
    }

    /// <summary>
    /// Counts one request against <paramref name="policy"/>.
    /// </summary>
    /// <param name="partition">
    /// The IP, or the user id for a per-user policy. A null partition is
    /// allowed through: we would otherwise put every anonymous caller in one
    /// bucket and rate-limit them as a single client.
    /// </param>
    public async Task<RateLimitVerdict> CheckAsync(
        RateLimitPolicy policy, string? partition, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(partition))
            return new RateLimitVerdict(true, policy.Limit, 0, null);

        // No "cbe:" here. ICacheStore adds the configured prefix to every key
        // it is given, so writing one in produced cbe:cbe:rl:* — which worked,
        // but meant the rate limiter was the one subsystem whose real key names
        // did not match the ones documented in SECURITY.md, and the only one
        // that would miss a prefix change.
        var key = $"rl:{policy.Name}:{partition}";

        try
        {
            var used = await cache.IncrementAsync(key, policy.Window, ct);

            if (used <= policy.Limit)
                return new RateLimitVerdict(true, policy.Limit, used, null);

            // The TTL is what is left of the window, so Retry-After is honest
            // rather than the full window every time.
            var ttl = await cache.TimeToLiveAsync(key, ct);
            return new RateLimitVerdict(false, policy.Limit, used, ttl ?? policy.Window);
        }
        catch (Exception)
        {
            // Fail open. See the remarks: a cache outage must not be an outage.
            return new RateLimitVerdict(true, policy.Limit, 0, null);
        }
    }
}
