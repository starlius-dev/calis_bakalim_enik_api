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

    private static RSA LoadOrCreateKey(JwtOptions options, ILogger logger)
    {
        var rsa = RSA.Create(2048);

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyPem))
        {
            rsa.ImportFromPem(options.PrivateKeyPem);
            return rsa;
        }

        // Development convenience only: persist a key so a restart does not
        // invalidate every token mid-debug. Production supplies the PEM through
        // the environment and never reaches this branch.
        if (File.Exists(options.DevKeyPath))
        {
            rsa.ImportFromPem(File.ReadAllText(options.DevKeyPath));
            return rsa;
        }

        File.WriteAllText(options.DevKeyPath, rsa.ExportRSAPrivateKeyPem());
        logger.LogWarning(
            "No Jwt:PrivateKeyPem configured — generated a development key at {Path}. " +
            "Production MUST supply one through the environment.",
            options.DevKeyPath);

        return rsa;
    }

    public void Dispose() => _rsa.Dispose();
}
