using System.Security.Cryptography;
using System.Text;
using CalisBakalimEnik.Application.Common.Interfaces;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Short-lived proof that the signed-in person just re-entered their password
/// (and second factor, when they have one), for one named action.
/// </summary>
/// <remarks>
/// Exporting every health record or deleting the account must not be possible
/// with a stolen session alone. A token is good for five minutes, one use, one
/// purpose and one user: a step-up done for an export cannot be replayed to
/// delete the account. Only its hash is stored.
/// </remarks>
public sealed class StepUpTokens(ICacheStore cache)
{
    public const string Export = "export";
    public const string Delete = "delete";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    public static bool IsPurpose(string? purpose) => purpose is Export or Delete;

    public async Task<string> IssueAsync(Guid userId, string purpose, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await cache.SetAsync(Key(token), Value(userId, purpose), Lifetime, ct);
        return token;
    }

    /// <summary>
    /// True once for a valid token. Removed on first presentation whatever the
    /// outcome, so a wrong guess also burns it.
    /// </summary>
    public async Task<bool> ConsumeAsync(
        string? token, Guid userId, string purpose, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        var key = Key(token);
        var stored = await cache.GetAsync(key, ct);
        if (stored is null) return false;

        await cache.RemoveAsync(key, ct);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(stored), Encoding.UTF8.GetBytes(Value(userId, purpose)));
    }

    private static string Key(string token) =>
        "stepup:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Value(Guid userId, string purpose) => $"{userId:N}:{purpose}";
}
