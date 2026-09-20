using System.Security.Claims;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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
        anon.MapPost("/forgot", ForgotAsync).AllowAnonymous();
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

        await auth.RevokeAllForUserAsync(user.Id, RefreshRevokedReason.PasswordChange, ct);

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
        SecurityEventWriter events,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email);

        if (user is not null && user.Status == UserStatus.Active)
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);

            await email.SendAsync(
                request.Email,
                "Çalış Bakalım Enik — şifre sıfırlama",
                $"userId={user.Id}{Environment.NewLine}token={token}",
                ct);
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
        if (user is null) return Results.NoContent();

        var result = await users.ResetPasswordAsync(user, request.Token, request.NewPassword);

        if (!result.Succeeded)
        {
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

        var sessions = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.IssuedAt)
            .Select(t => new SessionResponse(
                t.Id, null, t.UserAgent, t.CreatedIp, t.IssuedAt, t.ExpiresAt, false))
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
}
