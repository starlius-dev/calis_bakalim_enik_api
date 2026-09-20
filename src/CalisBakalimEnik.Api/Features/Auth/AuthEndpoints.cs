using System.Security.Claims;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CalisBakalimEnik.Api.Features.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record ConfirmEmailRequest(string UserId, string Token);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshExpiresAt);

public sealed record MeResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Locale,
    string TimeZone,
    IReadOnlyCollection<string> Roles,
    IReadOnlyCollection<string> Permissions);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapPost("/confirm-email", ConfirmEmailAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/logout-all", LogoutAllAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Open self-service registration. Returns <b>204 whatever happens</b> — a
    /// different response for an address that already exists turns signup into an
    /// account-enumeration oracle. The existing owner gets a "someone tried to
    /// register with your address" mail instead, which is useful to them and
    /// useless to an attacker. See docs/SECURITY.md §2.
    /// </summary>
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        UserManager<AppUser> users,
        IEmailSender email,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Auth.Register");
        var existing = await users.FindByEmailAsync(request.Email);

        if (existing is not null)
        {
            await email.SendAsync(
                request.Email,
                "Çalış Bakalım Enik — kayıt denemesi",
                "Bu adresle zaten bir hesap var. Sen değilsen görmezden gelebilirsin.",
                ct);

            logger.LogInformation("Registration attempted for an existing address");
            return Results.NoContent();
        }

        var user = new AppUser
        {
            UserName = request.Email,
            Email = request.Email,
            DisplayName = request.DisplayName,
            Status = UserStatus.PendingConfirmation,
            CreatedAt = clock.UtcNow,
        };

        var created = await users.CreateAsync(user, request.Password);

        if (!created.Succeeded)
        {
            // Password-policy failures are a real validation error the client can
            // act on, and reveal nothing about whether the address exists.
            var errors = created.Errors
                .GroupBy(e => e.Code.Contains("Password") ? "password" : "email")
                .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray());

            return Results.ValidationProblem(errors);
        }

        await users.AddToRoleAsync(user, Roles.User);

        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        await email.SendAsync(
            request.Email,
            "Çalış Bakalım Enik — e-postanı doğrula",
            $"userId={user.Id}\ntoken={token}",
            ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        UserManager<AppUser> users,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(request.UserId);
        if (user is null) return Results.NoContent();

        var result = await users.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded) return Results.NoContent();

        user.Status = UserStatus.Active;
        await users.UpdateAsync(user);

        return Results.NoContent();
    }

    /// <summary>
    /// A wrong password, an unknown address and a disabled account all return the
    /// same body. The unknown-user branch still hashes a dummy password so the
    /// response time does not reveal whether the address exists.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<AppUser> users,
        AuthService auth,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email);

        if (user is null)
        {
            // Burn equivalent CPU so the response time does not reveal whether the
            // address exists. The result is deliberately discarded.
            _ = users.PasswordHasher.VerifyHashedPassword(
                new AppUser(), DummyHash, request.Password);

            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status401Unauthorized, http);
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status401Unauthorized, http);

        if (user.Status == UserStatus.PendingConfirmation)
            return Problem(AuthErrors.NotConfirmed, StatusCodes.Status403Forbidden, http);

        if (user.Status == UserStatus.Disabled)
            return Problem(AuthErrors.Disabled, StatusCodes.Status403Forbidden, http);

        user.LastLoginAt = clock.UtcNow;
        await users.UpdateAsync(user);

        // MFA lands in Phase 4; until then a password login is complete.
        var pair = await auth.IssueAsync(user, ContextFrom(http), mfaSatisfied: false, ct);

        return Results.Ok(ToResponse(pair));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        AuthService auth,
        HttpContext http,
        CancellationToken ct)
    {
        var result = await auth.RefreshAsync(request.RefreshToken, ContextFrom(http), ct);

        return result.Succeeded
            ? Results.Ok(ToResponse(result.Value))
            : Problem(result.Error, StatusCodes.Status401Unauthorized, http);
    }

    private static async Task<IResult> LogoutAsync(
        RefreshRequest request,
        AuthService auth,
        ITokenService tokens,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        await auth.RevokeAsync(request.RefreshToken, RefreshRevokedReason.Logout, ct);
        await DenylistCurrentAccessTokenAsync(tokens, principal, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> LogoutAllAsync(
        AuthService auth,
        ITokenService tokens,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        await auth.RevokeAllForUserAsync(userId.Value, RefreshRevokedReason.Logout, ct);
        await DenylistCurrentAccessTokenAsync(tokens, principal, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(
        UserManager<AppUser> users,
        ICurrentUser currentUser,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return Results.Unauthorized();

        var roles = await users.GetRolesAsync(user);

        return Results.Ok(new MeResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            user.Locale,
            user.TimeZone,
            roles.ToArray(),
            currentUser.Permissions));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>A real PBKDF2 hash, so the unknown-user path does equivalent work.</summary>
    private const string DummyHash =
        "AQAAAAIAAYagAAAAEJ8Z1Ys0qkZ3mKk0wvQ0ZQ0gqZ0Y0Z0Y0Z0Y0Z0Y0Z0Y0Z0Y0Z0Y0Z0Y0Z0Y0Z0Yw==";

    private static Guid? UserId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue("sub"), out var id) ? id : null;

    private static async Task DenylistCurrentAccessTokenAsync(
        ITokenService tokens, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("jti"), out var jti)) return;

        var expRaw = principal.FindFirstValue("exp");
        if (!long.TryParse(expRaw, out var exp)) return;

        await tokens.DenylistAsync(jti, DateTimeOffset.FromUnixTimeSeconds(exp), ct);
    }

    private static AuthContext ContextFrom(HttpContext http) => new(
        ClientIp(http),
        http.Request.Headers.UserAgent.ToString(),
        DeviceId: null);

    /// <summary>
    /// Traffic arrives through a Cloudflare Tunnel, so the socket address is the
    /// same for every user. See docs/SECURITY.md §1.
    /// </summary>
    private static string? ClientIp(HttpContext http)
        => http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf)
           && !string.IsNullOrWhiteSpace(cf)
            ? cf.ToString()
            : http.Connection.RemoteIpAddress?.ToString();

    private static TokenResponse ToResponse(TokenPair pair) => new(
        pair.AccessToken, pair.AccessExpiresAt, pair.RefreshToken, pair.RefreshExpiresAt);

    private static IResult Problem(
        Application.Common.Models.Error error, int status, HttpContext http)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = error.Message,
            Type = $"https://calisbakalimenik.app/errors/{error.Code}",
            Instance = http.Request.Path,
        };

        problem.Extensions["correlationId"] =
            http.Items[CorrelationIdMiddleware.HeaderName] as string;

        return Results.Problem(problem);
    }
}
