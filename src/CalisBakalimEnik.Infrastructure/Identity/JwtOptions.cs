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
    /// Where the signing key is kept, used when <see cref="PrivateKeyPem"/> is
    /// not supplied. Created on first use if it does not exist.
    /// </summary>
    /// <remarks>
    /// This must be DURABLE STORAGE, outside the directory a deploy replaces.
    /// The key is what every issued access token is signed with, so losing it
    /// signs everybody out — and a path inside the app directory is lost on the
    /// next release, because the swap deletes whatever the new build does not
    /// contain. It is also the reason a PEM in the environment is awkward here:
    /// systemd reads an EnvironmentFile literally, and a PEM is multi-line.
    /// </remarks>
    public string? PrivateKeyPath { get; set; }
}
