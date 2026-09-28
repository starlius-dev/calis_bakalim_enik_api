using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CalisBakalimEnik.Infrastructure.Identity;

public sealed class TokenService : ITokenService, IDisposable
{
    public const string RoleClaimType = "role";
    public const string DeletionPendingClaim = "del";

    private readonly JwtOptions _options;
    private readonly IClock _clock;
    private readonly ICacheStore _cache;
    private readonly RSA _rsa;

    public TokenService(
        IOptions<JwtOptions> options,
        IClock clock,
        ICacheStore cache,
        ILogger<TokenService> logger)
    {
        _options = options.Value;
        _clock = clock;
        _cache = cache;
        _rsa = LoadOrCreateKey(_options, logger);
    }

    public SecurityKey PublicKey => new RsaSecurityKey(_rsa.ExportParameters(false));

    public string CreateAccessToken(
        AccessTokenSubject subject, out DateTimeOffset expiresAt, out Guid jti)
    {
        var now = _clock.UtcNow;
        expiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        jti = Guid.CreateVersion7();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject.UserId.ToString()),
            new(JwtRegisteredClaimNames.Jti, jti.ToString()),
            new(JwtRegisteredClaimNames.Email, subject.Email),
            new("name", subject.DisplayName),
            new("sid", subject.SessionId.ToString()),
            new("ver", "1"),
        };

        // Short "role", not ClaimTypes.Role: the latter is a 50-character
        // Microsoft schema URI repeated once per role, and the access token
        // travels on every request.
        claims.AddRange(subject.Roles.Select(r => new Claim(RoleClaimType, r)));
        claims.AddRange(subject.Permissions.Select(p => new Claim(Permissions.ClaimType, p)));

        // amr records HOW the user authenticated, so a handler can require a
        // second factor for a sensitive action without re-reading the database.
        claims.Add(new Claim("amr", "pwd"));
        if (subject.MfaSatisfied) claims.Add(new Claim("amr", "mfa"));

        if (subject.DeletionPending) claims.Add(new Claim(DeletionPendingClaim, "1"));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncoder.Encode(bytes);
    }

    public string HashRefreshToken(string token)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public async Task DenylistAsync(
        Guid jti, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        // TTL equals the token's REMAINING life, so the denylist stays bounded
        // without a sweeper. See docs/SECURITY.md §4.
        var ttl = expiresAt - _clock.UtcNow;
        if (ttl <= TimeSpan.Zero) return;

        await _cache.SetAsync($"jwt:denylist:{jti}", "1", ttl, ct);
    }

    public Task<bool> IsDenylistedAsync(Guid jti, CancellationToken ct = default)
        => _cache.ExistsAsync($"jwt:denylist:{jti}", ct);

    public async Task RevokeIssuedBeforeAsync(
        Guid userId, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        // Held only for as long as a token issued before the cutoff could still
        // be inside its own lifetime. After that the expiry does the work and
        // the key is dead weight.
        var ttl = TimeSpan.FromMinutes(_options.AccessTokenMinutes) + TimeSpan.FromMinutes(1);

        await _cache.SetAsync(
            $"jwt:cutoff:{userId}",
            cutoff.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            ttl,
            ct);
    }

    public async Task<DateTimeOffset?> IssuedBeforeCutoffAsync(
        Guid userId, CancellationToken ct = default)
    {
        var raw = await _cache.GetAsync($"jwt:cutoff:{userId}", ct);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)
            ? DateTimeOffset.FromUnixTimeSeconds(at)
            : null;
    }

    private static RSA LoadOrCreateKey(JwtOptions options, ILogger logger)
    {
        var rsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            rsa.ImportFromPem(options.PrivateKeyPem);
            return rsa;
        }

        // Otherwise the key lives in a file, created once and then reused. The
        // path has to be durable storage: this key signs every access token, so
        // replacing it signs every user out, and a deploy deletes anything in
        // the app directory that the new build does not carry.
        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                "Neither Jwt:PrivateKeyPem nor Jwt:PrivateKeyPath is configured, so "
                + "there is nothing to sign tokens with. Point Jwt:PrivateKeyPath at "
                + "a file on durable storage.");
        }

        if (File.Exists(options.PrivateKeyPath))
        {
            rsa.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
            return rsa;
        }

        var directory = Path.GetDirectoryName(options.PrivateKeyPath);

        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(options.PrivateKeyPath, rsa.ExportRSAPrivateKeyPem());

        // A private key readable by anything else on the box is a key anything
        // else on the box can mint tokens with.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                options.PrivateKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        logger.LogWarning(
            "No signing key existed — generated one at {Path}. Back it up with the "
            + "database: losing it invalidates every token and signs everybody out.",
            options.PrivateKeyPath);

        return rsa;
    }

    public void Dispose() => _rsa.Dispose();
}
