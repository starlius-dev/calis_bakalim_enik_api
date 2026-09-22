using System.Security.Claims;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Features.Auth;

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string UserId, string Token, string NewPassword);

public sealed record SessionResponse(
    Guid Id,
    string? DeviceName,
    string? UserAgent,
    string? Ip,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    bool IsCurrent);

/// <summary>
/// Backs the design's *Hesap ve güvenlik* (14, D11) and *Şifre değiştir* (15)
/// screens, including the OTURUMLAR group and its "Tüm oturumları kapat" action.
/// </summary>
public static class AccountSecurityEndpoints
{
    public static IEndpointRouteBuilder MapAccountSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var anon = app.MapGroup("/api/v1/auth/password").WithTags("Account");
        anon.MapPost("/forgot", ForgotAsync)
            .AllowAnonymous()
            .RateLimit(RateLimitGuard.Policies.ForgotPassword);
        anon.MapPost("/reset", ResetAsync).AllowAnonymous();

        var auth = app.MapGroup("/api/v1/auth").WithTags("Account").RequireAuthorization();
        auth.MapPost("/password/change", ChangeAsync);
        auth.MapGet("/sessions", ListSessionsAsync);
        auth.MapDelete("/sessions/{id:guid}", RevokeSessionAsync);

        return app;
    }

    /// <summary>
    /// Changing the password revokes every OTHER session. Leaving them alive
    /// defeats the main reason people change a password: they believe it leaked.
    /// </summary>
    private static async Task<IResult> ChangeAsync(
        ChangePasswordRequest request,
        UserManager<AppUser> users,
        AuthService auth,
        SecurityEventWriter events,
        IClock clock,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return Results.Unauthorized();

        var result = await users.ChangePasswordAsync(
            user, request.CurrentPassword, request.NewPassword);

        if (!result.Succeeded)
        {
            await events.WriteAsync(SecurityEventType.PasswordChanged, succeeded: false,
                user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http), null, ct);

            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = result.Errors.Select(e => e.Description).ToArray(),
            });
        }

        user.PasswordChangedAt = clock.UtcNow;
        await users.UpdateAsync(user);

        // Every OTHER session, not this one: see RevokeAllForUserAsync.
        await auth.RevokeAllForUserAsync(
            user.Id, RefreshRevokedReason.PasswordChange, ct, SessionId(principal));

        await events.WriteAsync(SecurityEventType.PasswordChanged, succeeded: true,
            user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http), null, ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Always 204, whether or not the address is registered. The screen says so
    /// out loud, which is better UX than silence and leaks nothing.
    /// </summary>
    private static async Task<IResult> ForgotAsync(
        ForgotPasswordRequest request,
        UserManager<AppUser> users,
        IEmailSender email,
        IOptions<EmailOptions> links,
        SecurityEventWriter events,
        ILogger<ForgotPasswordRequest> logger,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email);

        if (user is not null && user.Status == UserStatus.Active)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);

            try
            {
                await email.SendAsync(
                    request.Email,
                    "Çalış Bakalım Enik — şifre sıfırlama",
                    $"""
                     Şifreni sıfırlamak için aşağıdaki bağlantıya tıkla:

                     {EmailLinks.ResetPassword(links.Value, user.Id, token)}

                     Bağlantı 30 dakika geçerli. Bu isteği sen yapmadıysan
                     hiçbir şey yapmana gerek yok — şifren değişmedi.
                     """,
                    ct);
            }
            catch (Exception e)
            {
                // Same reasoning as registration: this endpoint answers 204 for
                // every address, and a provider outage must not turn the known
                // ones into 500s.
                logger.LogError(e, "Password reset mail failed to send");
            }
        }

        await events.WriteAsync(SecurityEventType.PasswordResetRequested, succeeded: true,
            user?.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
            new { known = user is not null }, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ResetAsync(
        ResetPasswordRequest request,
        UserManager<AppUser> users,
        AuthService auth,
        SecurityEventWriter events,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(request.UserId);

        // Same shape as confirmation: an unknown user and a dead link are
        // indistinguishable, but a real person is told their link expired rather
        // than being shown success and left with the old password.
        if (user is null)
        {
            return MfaEndpoints.Problem(
                AuthErrors.InvalidConfirmationToken,
                StatusCodes.Status400BadRequest,
                http);
        }

        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);

        if (!result.Succeeded)
        {
            // Identity reports a bad token and a weak password through the same
            // channel; the client shows whichever it gets against the field.
            var invalidToken = result.Errors.Any(e => e.Code.Contains("Token"));

            if (invalidToken)
            {
                return MfaEndpoints.Problem(
                    AuthErrors.InvalidConfirmationToken,
                    StatusCodes.Status400BadRequest,
                    http);
            }

            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["password"] = result.Errors.Select(e => e.Description).ToArray(),
            });
        }

        user.PasswordChangedAt = clock.UtcNow;
        await users.UpdateAsync(user);

        // A reset means the old password is suspect, so every session goes.
        await auth.RevokeAllForUserAsync(user.Id, RefreshRevokedReason.PasswordChange, ct);

        await events.WriteAsync(SecurityEventType.PasswordChanged, succeeded: true,
            user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
            new { via = "reset" }, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ListSessionsAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var now = clock.UtcNow;
        var currentFamily = SessionId(principal);

        // One live row per family — rotation revokes the row it replaces — so
        // this is a list of sign-ins, not of rotations.
        var sessions = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new SessionResponse(
                t.Id, null, t.UserAgent, t.CreatedIp, t.IssuedAt, t.ExpiresAt,
                currentFamily != null && t.FamilyId == currentFamily))
            .ToListAsync(ct);

        return Results.Ok(sessions);
    }

    private static async Task<IResult> RevokeSessionAsync(
        Guid id,
        AppDbContext db,
        IClock clock,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Scoped by UserId as well as id: without it, any authenticated user
        // could revoke anyone's session by guessing an id.
        var revoked = await db.RefreshTokens
            .Where(t => t.Id == id && t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, clock.UtcNow)
                      .SetProperty(t => t.RevokedReason, RefreshRevokedReason.Logout),
                ct);

        if (revoked == 0) return Results.NotFound();

        await events.WriteAsync(SecurityEventType.Logout, succeeded: true,
            userId, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
            new { scope = "single_session" }, ct);

        return Results.NoContent();
    }

    /// <summary>The refresh-token family the caller's access token was issued from.</summary>
    private static Guid? SessionId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue("sid"), out var id) ? id : null;
}
