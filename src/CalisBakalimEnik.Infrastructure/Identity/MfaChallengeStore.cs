using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;

namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed record MfaChallenge(
    Guid UserId,
    Guid FactorId,
    MfaFactorType FactorType,
    string? CodeHash,
    int Attempts);

/// <summary>
/// A pending MFA challenge lives in Redis with a TTL, not in PostgreSQL: it is
/// short-lived, high-churn and worthless after expiry, so a table buys nothing
/// and creates a vacuum burden. The OUTCOME is written to security_events.
///
/// The challenge id is not a token. It grants nothing but the right to attempt a
/// second factor, for one user, for five minutes, a bounded number of times.
/// </summary>
public sealed class MfaChallengeStore(ICacheStore cache)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxAttempts = 5;

    /// <summary>Resend cooldown, enforced server-side — the client's countdown is a courtesy.</summary>
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public async Task<Guid> CreateAsync(
        Guid userId, Guid factorId, MfaFactorType type, string? code, CancellationToken ct)
    {
        var challengeId = Guid.CreateVersion7();

        var challenge = new MfaChallenge(
            userId, factorId, type, code is null ? null : Hash(code), Attempts: 0);

        await cache.SetAsync(
            Key(challengeId), JsonSerializer.Serialize(challenge), Lifetime, ct);

        return challengeId;
    }

    public async Task<MfaChallenge?> GetAsync(Guid challengeId, CancellationToken ct)
    {
        var raw = await cache.GetAsync(Key(challengeId), ct);
        return raw is null ? null : JsonSerializer.Deserialize<MfaChallenge>(raw);
    }

    /// <summary>Returns the attempt number, and invalidates the challenge at the cap.</summary>
    public async Task<int> RecordAttemptAsync(
        Guid challengeId, MfaChallenge challenge, CancellationToken ct)
    {
        var attempts = challenge.Attempts + 1;

        if (attempts >= MaxAttempts)
        {
            // Exhausted: the challenge dies rather than being silently re-issued,
            // so the client has to start from the password step again.
            await cache.RemoveAsync(Key(challengeId), ct);
            return attempts;
        }

        var ttl = await cache.TimeToLiveAsync(Key(challengeId), ct) ?? Lifetime;
        await cache.SetAsync(
            Key(challengeId),
            JsonSerializer.Serialize(challenge with { Attempts = attempts }),
            ttl,
            ct);

        return attempts;
    }

    public Task ConsumeAsync(Guid challengeId, CancellationToken ct)
        => cache.RemoveAsync(Key(challengeId), ct);

    public static bool CodeMatches(MfaChallenge challenge, string code)
    {
        if (challenge.CodeHash is null) return false;

        // Constant-time comparison; a plain == on a 6-digit code leaks timing.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(challenge.CodeHash),
            Encoding.UTF8.GetBytes(Hash(code)));
    }

    /// <summary>Six digits, uniformly distributed. Not Random — this is a credential.</summary>
    public static string GenerateNumericCode()
        => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public async Task<bool> TryStartResendCooldownAsync(
        Guid userId, MfaFactorType type, CancellationToken ct)
    {
        var key = $"otp:send:{userId}:{(short)type}";
        if (await cache.ExistsAsync(key, ct)) return false;

        await cache.SetAsync(key, "1", ResendCooldown, ct);
        return true;
    }

    /// <summary>
    /// Records a spent TOTP time step so a code sniffed in transit cannot be
    /// replayed inside its own validity window.
    /// </summary>
    public async Task<bool> TryConsumeTotpStepAsync(
        Guid factorId, long step, CancellationToken ct)
    {
        var key = $"mfa:totp:used:{factorId}:{step}";
        if (await cache.ExistsAsync(key, ct)) return false;

        await cache.SetAsync(key, "1", TimeSpan.FromSeconds(90), ct);
        return true;
    }

    private static string Key(Guid challengeId) => $"mfa:chal:{challengeId}";

    private static string Hash(string code)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
