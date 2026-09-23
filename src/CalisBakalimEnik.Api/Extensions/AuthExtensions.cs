using System.Globalization;
using System.Security.Claims;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CalisBakalimEnik.Api.Extensions;

public static class AuthExtensions
{
    public static IServiceCollection AddAuthSetup(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // The signing key lives in TokenService, which owns the RSA instance.
        // Resolving it through IConfigureOptions rather than inside the handler
        // keeps it on the ONE container — calling BuildServiceProvider() here
        // would create a second one, duplicating every singleton and leaking it.
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearer>();

        services.AddAuthorization(options =>
        {
            // One policy per permission. Adding a permission never touches the
            // call sites, which only reference the policy name.
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy =>
                    policy.RequireClaim(Permissions.ClaimType, permission));
            }
        });

        return services;
    }
}

internal sealed class ConfigureJwtBearer(
    IOptions<JwtOptions> jwtOptions,
    IServiceProvider services) : IConfigureNamedOptions<JwtBearerOptions>
{
    public void Configure(JwtBearerOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, JwtBearerOptions options)
    {
        if (name != JwtBearerDefaults.AuthenticationScheme) return;

        var jwt = jwtOptions.Value;

        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,

            // The default is FIVE MINUTES, which silently extends every token
            // past its stated expiry. See docs/SECURITY.md §4.
            ClockSkew = TimeSpan.Zero,

            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RoleClaimType = TokenService.RoleClaimType,
            NameClaimType = "name",

            IssuerSigningKeyResolver = (_, _, _, _) =>
            {
                var tokenService = (TokenService)services.GetRequiredService<ITokenService>();
                return [tokenService.PublicKey];
            },
        };
    }
}

/// <summary>
/// Stateless tokens cannot be un-issued, so two Redis lookups stand in for it.
/// </summary>
/// <remarks>
/// <para>The <b>denylist</b> revokes one token by its jti. It can only ever
/// revoke a token somebody is holding, which in practice means the caller's
/// own — logout, and the sign-out that follows a password change.</para>
///
/// <para>The <b>cutoff</b> revokes every token already issued to a user,
/// because nothing tracks the jti of a token issued to another session.
/// Without it, disabling an account or demoting an admin cut only the refresh
/// tokens and left the access tokens valid for the rest of their fifteen
/// minutes.</para>
///
/// <para>Two round trips to Redis per authenticated request, both O(1) and
/// both against a local instance in production. That is the price of being
/// able to revoke a session that is not the one asking.</para>
/// </remarks>
public sealed class JwtDenylistMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context, ITokenService tokens, ILogger<JwtDenylistMiddleware> logger)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            bool revoked;

            try
            {
                revoked = await IsRevokedAsync(context, tokens);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // FAIL OPEN, deliberately - and this is the interesting line in
                // the file.
                //
                // Both lookups live in Redis, which is a cache here and not a
                // system of record. Letting a cache fault answer 500 turns a
                // blip into a total outage of every authenticated route, and
                // that is not hypothetical: one dropped connection did exactly
                // that, answering 500 on every request after a five-second
                // stall apiece.
                //
                // The cost of the other direction is bounded and small. During
                // an outage a session revoked in the last few minutes keeps
                // working until its access token expires, which is at most
                // Jwt:AccessTokenMinutes. The refresh token lives in PostgreSQL
                // and is still revoked, so nothing can be renewed past that
                // window. RateLimitGuard makes the same call for the same
                // reason.
                logger.LogWarning(ex,
                    "Revocation cache unreachable; allowing the request. Path={Path}",
                    context.Request.Path);

                revoked = false;
            }

            if (revoked)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context);
    }

    /// <summary>
    /// The two revocation questions, asked together so one catch covers both.
    /// </summary>
    private static async Task<bool> IsRevokedAsync(HttpContext context, ITokenService tokens)
    {
        var raw = context.User.FindFirst("jti")?.Value;

        if (Guid.TryParse(raw, out var jti) && await tokens.IsDenylistedAsync(jti))
            return true;

        return await IsBeforeCutoffAsync(context, tokens);
    }

    /// <summary>
    /// Whether this token was issued before its user's cutoff.
    /// </summary>
    /// <remarks>
    /// Compared on <c>nbf</c>, which the token carries and which the handler
    /// has already validated. A token with no readable <c>nbf</c> is treated as
    /// revoked rather than as exempt — the alternative is a token that opts out
    /// of revocation by omitting a claim.
    /// </remarks>
    private static async Task<bool> IsBeforeCutoffAsync(
        HttpContext context, ITokenService tokens)
    {
        var subject = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? context.User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(subject, out var userId)) return false;

        var cutoff = await tokens.IssuedBeforeCutoffAsync(userId, context.RequestAborted);

        return IsRevoked(cutoff, context.User.FindFirst("nbf")?.Value);
    }

    /// <summary>
    /// Whether a token issued at <paramref name="notBefore"/> falls before the
    /// user's cutoff.
    /// </summary>
    /// <remarks>
    /// <para>A token whose <c>nbf</c> cannot be read is treated as REVOKED, not
    /// as exempt. The opposite reading would let a token opt out of revocation
    /// by omitting a claim, which is the wrong way for this to fail.</para>
    ///
    /// <para><b>The comparison includes the boundary second.</b> <c>nbf</c> has
    /// one-second resolution, so a strict <c>&lt;</c> lets any token minted in
    /// the same second as the revocation survive — for its whole fifteen
    /// minutes, not for a second. That is not theoretical: disabling an account
    /// immediately after its owner signed in does exactly that, and the first
    /// version of this shipped with it.</para>
    ///
    /// <para>The cost of <c>&lt;=</c> is a user who signs in again inside the
    /// same second as their own "sign out everywhere" getting one dead token
    /// and having to repeat it. Against a revoked session staying live for a
    /// quarter of an hour, that is not a close call.</para>
    /// </remarks>
    public static bool IsRevoked(DateTimeOffset? cutoff, string? notBefore)
    {
        if (cutoff is null) return false;

        return !long.TryParse(
                   notBefore, NumberStyles.Integer, CultureInfo.InvariantCulture, out var at)
               || DateTimeOffset.FromUnixTimeSeconds(at) <= cutoff.Value;
    }
}
