using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed record LockoutState(bool IsLocked, TimeSpan? RetryAfter);

/// <summary>
/// The three independent brute-force layers from docs/SECURITY.md §5.
///
/// Counters live in Redis, not in Identity's <c>AccessFailedCount</c>: that is a
/// database write on every failed attempt, which hands an attacker free
/// write-amplification. Nothing here is authoritative — a Redis flush resets
/// counters and loses no user data.
/// </summary>
public sealed class BruteForceGuard(
    ICacheStore cache,
    ILogger<BruteForceGuard> logger)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    /// <summary>Per-IP: 20 failures across ANY accounts blocks the address.</summary>
    private const int IpThreshold = 20;
    private static readonly TimeSpan IpBlock = TimeSpan.FromMinutes(30);

    public async Task<LockoutState> CheckAsync(string accountKey, string? ip, CancellationToken ct)
    {
        var accountTtl = await cache.TimeToLiveAsync(LockKey(accountKey), ct);
        if (accountTtl is not null) return new LockoutState(true, accountTtl);

        if (ip is not null)
        {
            var ipTtl = await cache.TimeToLiveAsync(IpLockKey(ip), ct);
            if (ipTtl is not null) return new LockoutState(true, ipTtl);
        }

        return new LockoutState(false, null);
    }

    /// <summary>Returns the lockout now in force, if this failure triggered one.</summary>
    public async Task<LockoutState> RecordFailureAsync(
        string accountKey, string? ip, CancellationToken ct)
    {
        var failures = await cache.IncrementAsync(FailKey(accountKey), Window, ct);
        var penalty = PenaltyFor(failures);

        if (penalty is not null)
        {
            await cache.SetAsync(LockKey(accountKey), "1", penalty.Value, ct);
            logger.LogWarning(
                "Account locked after {Failures} failures for {Penalty}",
                failures, penalty);
        }

        // Layer 2 — the per-account limit does nothing against enumeration:
        // spraying one password across 5,000 addresses never trips it.
        if (ip is not null)
        {
            var ipFailures = await cache.IncrementAsync(IpFailKey(ip), Window, ct);

            if (ipFailures >= IpThreshold)
            {
                await cache.SetAsync(IpLockKey(ip), "1", IpBlock, ct);
                logger.LogWarning("IP blocked after {Failures} failures", ipFailures);
                return new LockoutState(true, IpBlock);
            }
        }

        return penalty is null
            ? new LockoutState(false, null)
            : new LockoutState(true, penalty);
    }

    /// <summary>Cleared only on a COMPLETE login — password and, when enrolled, MFA.</summary>
    public async Task ResetAsync(string accountKey, CancellationToken ct)
    {
        await cache.RemoveAsync(FailKey(accountKey), ct);
        await cache.RemoveAsync(LockKey(accountKey), ct);
    }

    /// <summary>
    /// Escalating, and capped at 15 minutes.
    ///
    /// A permanent lock requiring an admin is deliberately NOT implemented: any
    /// per-account lockout lets someone who knows an address lock its owner out,
    /// so the first penalty is small and the ceiling is low. An existing session
    /// is not invalidated by a lockout either.
    /// </summary>
    private static TimeSpan? PenaltyFor(long failures) => failures switch
    {
        < 5 => null,
        5 => TimeSpan.FromMinutes(1),
        6 => TimeSpan.FromMinutes(2),
        7 => TimeSpan.FromMinutes(4),
        8 => TimeSpan.FromMinutes(8),
        _ => TimeSpan.FromMinutes(15),
    };

    private static string FailKey(string account) => $"login:fail:{account}";
    private static string LockKey(string account) => $"login:lock:{account}";
    private static string IpFailKey(string ip) => $"login:fail:ip:{ip}";
    private static string IpLockKey(string ip) => $"login:lock:ip:{ip}";
}
