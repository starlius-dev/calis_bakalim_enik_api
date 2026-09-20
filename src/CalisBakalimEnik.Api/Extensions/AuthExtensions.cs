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
/// Stateless tokens cannot be un-issued, so logout-everywhere, password change
/// and role change put the jti on a Redis denylist. One EXISTS per request, and
/// bounded in size because entries expire on their own.
/// </summary>
public sealed class JwtDenylistMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITokenService tokens)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var raw = context.User.FindFirst("jti")?.Value;

            if (Guid.TryParse(raw, out var jti) && await tokens.IsDenylistedAsync(jti))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await next(context);
    }
}
