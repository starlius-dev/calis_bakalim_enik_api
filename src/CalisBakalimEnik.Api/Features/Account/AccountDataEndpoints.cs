using System.Reflection;
using System.Security.Claims;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Features.Account;

/// <param name="Password">Nullable on purpose; see <see cref="EnrolOtpRequest"/>.</param>
/// <param name="Purpose"><c>export</c> or <c>delete</c>.</param>
public sealed record StepUpBeginRequest(string? Password, string? Purpose);

public sealed record StepUpVerifyRequest(Guid ChallengeId, string? Code, string? Purpose);

/// <summary>
/// Either the token (no second factor on the account) or a challenge to answer,
/// shaped like the login MFA response so the client reuses the same screen.
/// </summary>
public sealed record StepUpResponse(
    string? StepUpToken,
    bool MfaRequired,
    Guid? ChallengeId,
    IReadOnlyList<FactorResponse>? Factors);

public sealed record DeletionScheduledResponse(DateTimeOffset ScheduledFor);

/// <summary>
/// The person's own data: download it (D16) and delete the account (D15).
/// </summary>
/// <remarks>
/// <para>Both sit behind a step-up: password again, then the second factor when
/// the account has one. The result is a five-minute, single-use token for one
/// named purpose, sent as <c>X-Step-Up</c>. A stolen session alone can neither
/// take the whole health history nor destroy it.</para>
///
/// <para>Deleting is a request, not an erasure: the account is locked and
/// signed out everywhere, and <see cref="AccountErasureJob"/> erases it
/// <see cref="AccountDeletionOptions.GraceDays"/> days later unless the owner
/// signs in and cancels. Everything here stays reachable while deletion is
/// pending; see <see cref="Middleware.DeletionPendingMiddleware"/>.</para>
/// </remarks>
public static class AccountDataEndpoints
{
    public const string StepUpHeader = "X-Step-Up";

    public static IEndpointRouteBuilder MapAccountDataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account")
            .WithTags("Account")
            .RequireAuthorization();

        group.MapPost("/step-up", BeginStepUpAsync).RateLimit(RateLimitGuard.Policies.Login);
        group.MapPost("/step-up/verify", VerifyStepUpAsync).RateLimit(RateLimitGuard.Policies.Login);
        group.MapPost("/export", ExportAsync);
        group.MapPost("/deletion", RequestDeletionAsync);
        group.MapDelete("/deletion", CancelDeletionAsync);

        return app;
    }

    // ── step-up ──────────────────────────────────────────────────────────

    private static async Task<IResult> BeginStepUpAsync(
        StepUpBeginRequest request,
        UserManager<AppUser> users,
        MfaService mfa,
        BruteForceGuard guard,
        StepUpTokens stepUps,
        SecurityEventWriter events,
        IOptions<MfaOptions> mfaOptions,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        if (!StepUpTokens.IsPurpose(request.Purpose))
            return BadPurpose();

        var user = await CurrentUserAsync(users, principal);
        if (user is null) return Results.Unauthorized();

        var ip = MfaEndpoints.ClientIp(http);
        var accountKey = MfaEndpoints.AccountKey(user.Email!);

        // The same counters as login: re-entering a password here must not be
        // a way round the lockout.
        var lockout = await guard.CheckAsync(accountKey, ip, ct);
        if (lockout.IsLocked) return AuthEndpoints.Locked(lockout, http);

        if (string.IsNullOrEmpty(request.Password)
            || !await users.CheckPasswordAsync(user, request.Password))
        {
            var state = await guard.RecordFailureAsync(accountKey, ip, ct);
            await events.WriteAsync(SecurityEventType.StepUp, succeeded: false,
                user.Id, ip, MfaEndpoints.UserAgent(http),
                new { purpose = request.Purpose, reason = "bad_password" }, ct);

            return state.IsLocked
                ? AuthEndpoints.Locked(state, http)
                : MfaEndpoints.Problem(AuthErrors.InvalidCredentials, StatusCodes.Status403Forbidden, http);
        }

        var factors = mfaOptions.Value.Enabled
            ? await mfa.GetUsableFactorsAsync(user.Id, ct)
            : [];

        if (factors.Count > 0)
        {
            var challengeId = await mfa.StartChallengeAsync(user.Id, factors[0], ct);
            var remaining = await mfa.CountRemainingCodesAsync(user.Id, ct);

            return Results.Ok(new StepUpResponse(
                null,
                MfaRequired: true,
                challengeId,
                factors.Select(f => new FactorResponse(
                    f.Id, f.FactorType.ToString(), f.IsPrimary, Verified: true,
                    f.MaskedDestination,
                    f.FactorType == MfaFactorType.RecoveryCode ? remaining : null)).ToArray()));
        }

        return Results.Ok(await IssueAsync(user.Id, request.Purpose!, guard, accountKey,
            stepUps, events, http, ct));
    }

    private static async Task<IResult> VerifyStepUpAsync(
        StepUpVerifyRequest request,
        UserManager<AppUser> users,
        MfaService mfa,
        BruteForceGuard guard,
        StepUpTokens stepUps,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        if (!StepUpTokens.IsPurpose(request.Purpose))
            return BadPurpose();

        var user = await CurrentUserAsync(users, principal);
        if (user is null) return Results.Unauthorized();

        var result = await mfa.VerifyChallengeAsync(request.ChallengeId, request.Code ?? string.Empty, ct);

        // The challenge must be this user's: a challenge id is not a
        // credential for anyone else's step-up.
        if (result.Failed || result.Value.UserId != user.Id)
        {
            await events.WriteAsync(SecurityEventType.StepUp, succeeded: false,
                user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
                new { purpose = request.Purpose, reason = result.Failed ? result.Error.Code : "foreign_challenge" }, ct);

            var error = result.Failed ? result.Error : MfaErrors.InvalidCode;
            var status = error.Code == MfaErrors.AttemptsExhausted.Code
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status403Forbidden;
            return MfaEndpoints.Problem(error, status, http);
        }

        return Results.Ok(await IssueAsync(user.Id, request.Purpose!, guard,
            MfaEndpoints.AccountKey(user.Email!), stepUps, events, http, ct));
    }

    private static async Task<StepUpResponse> IssueAsync(
        Guid userId, string purpose, BruteForceGuard guard, string accountKey,
        StepUpTokens stepUps, SecurityEventWriter events, HttpContext http, CancellationToken ct)
    {
        await guard.ResetAsync(accountKey, ct);
        var token = await stepUps.IssueAsync(userId, purpose, ct);

        await events.WriteAsync(SecurityEventType.StepUp, succeeded: true,
            userId, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
            new { purpose }, ct);

        return new StepUpResponse(token, MfaRequired: false, null, null);
    }

    // ── export (D16) ─────────────────────────────────────────────────────

    private static async Task<IResult> ExportAsync(
        StepUpTokens stepUps,
        UserDataExporter exporter,
        SecurityEventWriter events,
        IClock clock,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (!await stepUps.ConsumeAsync(http.Request.Headers[StepUpHeader], userId.Value, StepUpTokens.Export, ct))
            return StepUpRequired(http);

        var version = typeof(AccountDataEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "0.0.0";

        var zip = await exporter.ExportAsync(userId.Value, version, ct);

        await events.WriteAsync(SecurityEventType.DataExported, succeeded: true,
            userId, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
            new { bytes = zip.Length }, ct);

        return Results.File(zip, "application/zip",
            $"calis-bakalim-enik-verilerim-{clock.UtcNow:yyyy-MM-dd}.zip");
    }

    // ── deletion (D15) ───────────────────────────────────────────────────

    private static async Task<IResult> RequestDeletionAsync(
        UserManager<AppUser> users,
        StepUpTokens stepUps,
        AuthService auth,
        ITokenService tokens,
        SecurityEventWriter events,
        IEmailSender email,
        IOptions<AccountDeletionOptions> options,
        IClock clock,
        ILoggerFactory loggers,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(users, principal);
        if (user is null) return Results.Unauthorized();

        if (!await stepUps.ConsumeAsync(http.Request.Headers[StepUpHeader], user.Id, StepUpTokens.Delete, ct))
            return StepUpRequired(http);

        // Asking twice does not move the date: a second request is not a
        // reason to keep the data longer.
        if (user.DeletionScheduledAt is null)
        {
            var now = clock.UtcNow;
            user.DeletionScheduledAt = now.AddDays(Math.Max(1, options.Value.GraceDays));
            await users.UpdateAsync(user);

            // Signed out everywhere, this device included: the account is
            // locked from here, and anything further needs a fresh sign-in,
            // which is where cancelling happens.
            await auth.RevokeAllForUserAsync(user.Id, RefreshRevokedReason.AccountDeletion, ct);
            await tokens.RevokeIssuedBeforeAsync(user.Id, now, ct);

            await events.WriteAsync(SecurityEventType.AccountDeletionRequested, succeeded: true,
                user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
                new { scheduledFor = user.DeletionScheduledAt }, ct);

            await SendQuietlyAsync(email, loggers, user.Email, "Hesabın silinecek",
                AccountEmails.Requested(user.DisplayName, Local(user.DeletionScheduledAt.Value, user.TimeZone)), ct);
        }

        return Results.Ok(new DeletionScheduledResponse(user.DeletionScheduledAt.Value));
    }

    /// <summary>
    /// Cancels a pending deletion. Needs only the signed-in session: getting
    /// one already took the password and second factor, and making it harder
    /// to KEEP your data is the wrong way round.
    /// </summary>
    /// <remarks>
    /// The caller's tokens still carry the <c>del</c> claim; the client
    /// refreshes straight after, and the new pair is issued without it.
    /// </remarks>
    private static async Task<IResult> CancelDeletionAsync(
        UserManager<AppUser> users,
        SecurityEventWriter events,
        IEmailSender email,
        ILoggerFactory loggers,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(users, principal);
        if (user is null) return Results.Unauthorized();

        if (user.DeletionScheduledAt is null) return Results.NoContent();

        user.DeletionScheduledAt = null;
        await users.UpdateAsync(user);

        await events.WriteAsync(SecurityEventType.AccountDeletionCancelled, succeeded: true,
            user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http), null, ct);

        await SendQuietlyAsync(email, loggers, user.Email, "Hesap silme iptal edildi",
            AccountEmails.Cancelled(user.DisplayName), ct);

        return Results.NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<AppUser?> CurrentUserAsync(
        UserManager<AppUser> users, ClaimsPrincipal principal)
    {
        var userId = MfaEndpoints.UserId(principal);
        return userId is null ? null : await users.FindByIdAsync(userId.Value.ToString());
    }

    private static IResult BadPurpose() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["purpose"] = ["Geçersiz işlem."],
        });

    private static IResult StepUpRequired(HttpContext http) =>
        MfaEndpoints.Problem(
            new Error("account.step_up_required", "Bu işlem için şifreni yeniden girmen gerekiyor."),
            StatusCodes.Status403Forbidden,
            http);

    /// <summary>The date in the person's own zone, as the mail shows it.</summary>
    private static string Local(DateTimeOffset utc, string timeZone)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            return TimeZoneInfo.ConvertTime(utc, zone).ToString("dd.MM.yyyy HH:mm");
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return utc.ToString("dd.MM.yyyy HH:mm") + " (UTC)";
        }
    }

    /// <summary>A failed mail must not undo or fail the request itself.</summary>
    private static async Task SendQuietlyAsync(
        IEmailSender email, ILoggerFactory loggers, string? to, string subject, string body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to)) return;

        try
        {
            await email.SendAsync(to, subject, body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("Account.Deletion")
                .LogWarning(ex, "Deletion mail '{Subject}' could not be sent.", subject);
        }
    }
}
