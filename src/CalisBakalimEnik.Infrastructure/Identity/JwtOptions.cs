namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public int RefreshTokenAbsoluteCapDays { get; set; } = 90;

    /// <summary>
    /// PEM-encoded RSA private key. RS256 rather than HS256 so the signing key
    /// never leaves the API and any future service can verify with the public
    /// key alone. Supplied by environment variable in production.
    /// </summary>
    public string? PrivateKeyPem { get; set; }

    /// <summary>
    /// Where to persist a development key so restarts do not invalidate every
    /// token. Never used outside Development.
    /// </summary>
    public string DevKeyPath { get; set; } = "jwt-dev-key.pem";
}
