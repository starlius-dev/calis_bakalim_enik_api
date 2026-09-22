namespace CalisBakalimEnik.Application.Common.Interfaces;

public sealed record TokenPair(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public sealed record AccessTokenSubject(
    Guid UserId,
    string Email,
    string DisplayName,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions,
    bool MfaSatisfied,
    // The refresh-token FAMILY this access token was issued from. One family is
    // one sign-in on one device, because rotation revokes the row it replaces,
    // so exactly one live row per family. Carried as `sid` so the session list
    // can mark the caller's own row and a password change can spare it.
    Guid SessionId);

public interface ITokenService
{
    string CreateAccessToken(AccessTokenSubject subject, out DateTimeOffset expiresAt, out Guid jti);

    /// <summary>256 bits of CSPRNG output, base64url. Only its hash is persisted.</summary>
    string CreateRefreshToken();

    string HashRefreshToken(string token);

    /// <summary>Revokes an access token immediately by denylisting its jti in Redis.</summary>
    Task DenylistAsync(Guid jti, DateTimeOffset expiresAt, CancellationToken ct = default);

    Task<bool> IsDenylistedAsync(Guid jti, CancellationToken ct = default);

    /// <summary>
    /// Invalidates every access token already issued to a user.
    /// </summary>
    /// <remarks>
    /// The denylist can only revoke a token somebody is holding — the caller's
    /// own. Nothing tracks the jti of a token issued to a DIFFERENT session, so
    /// until this existed there was no way to revoke one: disabling an account,
    /// demoting an admin and signing out every device all cut the refresh
    /// tokens and left the access tokens alive for the rest of their fifteen
    /// minutes. A disabled account that keeps working for fifteen minutes is
    /// not disabled.
    ///
    /// One key per user rather than one per token: a cutoff instant, against
    /// which every token's <c>nbf</c> is compared. It expires on its own after
    /// the access-token lifetime, because a token older than that is refused by
    /// its own expiry anyway.
    /// </remarks>
    Task RevokeIssuedBeforeAsync(
        Guid userId, DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>The cutoff for a user, or null if there is none.</summary>
    Task<DateTimeOffset?> IssuedBeforeCutoffAsync(
        Guid userId, CancellationToken ct = default);
}
