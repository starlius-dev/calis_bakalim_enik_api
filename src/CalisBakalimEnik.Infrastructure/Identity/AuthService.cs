using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed record AuthContext(string? Ip, string? UserAgent, Guid? DeviceId);

/// <summary>
/// Issues and rotates the token pair.
///
/// The security property that matters here is <b>refresh reuse detection</b>:
/// every rotation stays inside one <c>FamilyId</c>, so presenting a token that
/// has already been rotated means either theft or a buggy client, and the whole
/// family is revoked. Without it, a stolen refresh token is usable until expiry.
/// See docs/SECURITY.md §4.
/// </summary>
public sealed class AuthService(
    AppDbContext db,
    UserManager<AppUser> users,
    ITokenService tokens,
    IClock clock,
    IOptions<JwtOptions> options,
    ILogger<AuthService> logger)
{
    private readonly JwtOptions _options = options.Value;

    public async Task<TokenPair> IssueAsync(
        AppUser user, AuthContext context, bool mfaSatisfied, CancellationToken ct)
    {
        var family = Guid.CreateVersion7();
        return await IssuePairAsync(user, family, null, context, mfaSatisfied, ct);
    }

    /// <summary>
    /// Rotates a refresh token. Returns a failure rather than throwing, because
    /// "this token is no longer valid" is an expected outcome, not an exception.
    /// </summary>
    public async Task<Result<TokenPair>> RefreshAsync(
        string refreshToken, AuthContext context, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(refreshToken);
        var now = clock.UtcNow;

        var stored = await db.RefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
            return Result.Failure<TokenPair>(AuthErrors.InvalidRefreshToken);

        // ── Reuse detection ──────────────────────────────────────────────
        // An already-revoked token being presented again means the chain leaked.
        // Revoke the ENTIRE family, not just this token: the attacker and the
        // legitimate client both hold descendants of it.
        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, RefreshRevokedReason.ReuseDetected, ct);

            logger.LogWarning(
                "Refresh token reuse detected. UserId={UserId} FamilyId={FamilyId} Ip={Ip}",
                stored.UserId, stored.FamilyId, context.Ip);

            return Result.Failure<TokenPair>(AuthErrors.RefreshReuseDetected);
        }

        if (stored.ExpiresAt <= now)
            return Result.Failure<TokenPair>(AuthErrors.InvalidRefreshToken);

        var user = await users.FindByIdAsync(stored.UserId.ToString());
        if (user is null || user.Status != UserStatus.Active)
            return Result.Failure<TokenPair>(AuthErrors.InvalidRefreshToken);

        var pair = await IssuePairAsync(
            user, stored.FamilyId, stored, context, mfaSatisfied: true, ct);

        return Result.Success(pair);
    }

    public async Task RevokeAsync(
        string refreshToken, RefreshRevokedReason reason, CancellationToken ct)
    {
        var hash = tokens.HashRefreshToken(refreshToken);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (stored is null || stored.RevokedAt is not null) return;

        stored.RevokedAt = clock.UtcNow;
        stored.RevokedReason = reason;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Logout-everywhere, password change, role change.
    /// </summary>
    /// <param name="exceptFamilyId">
    /// The caller's own session, spared. A password change revokes the OTHER
    /// sessions — signing the user out of the device they are actively typing
    /// on, moments after they proved the current password, is not security.
    /// A reset (where the old password is presumed stolen) passes null.
    /// </param>
    public async Task RevokeAllForUserAsync(
        Guid userId,
        RefreshRevokedReason reason,
        CancellationToken ct,
        Guid? exceptFamilyId = null)
    {
        var now = clock.UtcNow;
        await db.RefreshTokens
            .Where(t => t.UserId == userId
                        && t.RevokedAt == null
                        && (exceptFamilyId == null || t.FamilyId != exceptFamilyId))
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now)
                      .SetProperty(t => t.RevokedReason, reason),
                ct);
    }

    private async Task RevokeFamilyAsync(
        Guid familyId, RefreshRevokedReason reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, now)
                      .SetProperty(t => t.RevokedReason, reason),
                ct);
    }

    private async Task<TokenPair> IssuePairAsync(
        AppUser user,
        Guid familyId,
        RefreshToken? rotating,
        AuthContext context,
        bool mfaSatisfied,
        CancellationToken ct)
    {
        var now = clock.UtcNow;

        var roles = await users.GetRolesAsync(user);
        var permissions = await ResolvePermissionsAsync(roles, ct);

        var subject = new AccessTokenSubject(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            roles.ToArray(),
            permissions,
            mfaSatisfied,
            familyId);

        var accessToken = tokens.CreateAccessToken(subject, out var accessExpires, out _);

        var refreshValue = tokens.CreateRefreshToken();

        // Sliding expiry, but never past the family's absolute cap — otherwise a
        // token that is refreshed daily never expires at all.
        var familyStart = rotating is null
            ? now
            : await db.RefreshTokens
                .Where(t => t.FamilyId == familyId)
                .MinAsync(t => (DateTimeOffset?)t.IssuedAt, ct) ?? now;

        var slidingExpiry = now.AddDays(_options.RefreshTokenDays);
        var absoluteCap = familyStart.AddDays(_options.RefreshTokenAbsoluteCapDays);
        var refreshExpires = slidingExpiry < absoluteCap ? slidingExpiry : absoluteCap;

        var entity = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = tokens.HashRefreshToken(refreshValue),
            FamilyId = familyId,
            DeviceId = context.DeviceId,
            IssuedAt = now,
            ExpiresAt = refreshExpires,
            CreatedIp = context.Ip,
            UserAgent = context.UserAgent,
        };

        db.RefreshTokens.Add(entity);

        if (rotating is not null)
        {
            rotating.RevokedAt = now;
            rotating.RevokedReason = RefreshRevokedReason.Rotated;
            rotating.ReplacedById = entity.Id;
        }

        await db.SaveChangesAsync(ct);

        return new TokenPair(accessToken, accessExpires, refreshValue, refreshExpires);
    }

    private async Task<IReadOnlyCollection<string>> ResolvePermissionsAsync(
        IEnumerable<string> roleNames, CancellationToken ct)
    {
        var names = roleNames.ToArray();
        if (names.Length == 0) return [];

        return await db.Roles
            .Where(r => r.Name != null && names.Contains(r.Name))
            .Join(db.RoleClaims,
                r => r.Id,
                c => c.RoleId,
                (r, c) => c)
            .Where(c => c.ClaimType == Permissions.ClaimType && c.ClaimValue != null)
            .Select(c => c.ClaimValue!)
            .Distinct()
            .ToArrayAsync(ct);
    }
}

public static class AuthErrors
{
    public static readonly Error InvalidCredentials =
        new("auth.invalid_credentials", "E-posta veya şifre hatalı.");

    public static readonly Error NotConfirmed =
        new("auth.not_confirmed", "Hesabın henüz doğrulanmadı.");

    public static readonly Error Disabled =
        new("auth.disabled", "Bu hesap devre dışı.");

    public static readonly Error InvalidRefreshToken =
        new("auth.invalid_refresh_token", "Oturum süresi doldu. Tekrar giriş yap.");

    public static readonly Error InvalidConfirmationToken =
        new("auth.invalid_token", "Bu bağlantı geçersiz ya da süresi dolmuş.");

    public static readonly Error RefreshReuseDetected =
        new("auth.refresh_reuse", "Güvenlik nedeniyle tüm oturumlar kapatıldı.");
}
