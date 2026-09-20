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
    bool MfaSatisfied);

public interface ITokenService
{
    string CreateAccessToken(AccessTokenSubject subject, out DateTimeOffset expiresAt, out Guid jti);

    /// <summary>256 bits of CSPRNG output, base64url. Only its hash is persisted.</summary>
    string CreateRefreshToken();

    string HashRefreshToken(string token);

    /// <summary>Revokes an access token immediately by denylisting its jti in Redis.</summary>
    Task DenylistAsync(Guid jti, DateTimeOffset expiresAt, CancellationToken ct = default);

    Task<bool> IsDenylistedAsync(Guid jti, CancellationToken ct = default);
}
