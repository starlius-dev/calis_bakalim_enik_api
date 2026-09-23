using System.Security.Claims;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CalisBakalimEnik.Api.Features.Auth;

public sealed record MfaVerifyRequest(Guid ChallengeId, string Code);
public sealed record MfaResendRequest(Guid ChallengeId);
public sealed record MfaSelectRequest(Guid ChallengeId, Guid FactorId);
public sealed record ConfirmCodeRequest(string Code);

/// <param name="Password">
/// Nullable because the deserialiser makes it so, whatever the annotation
/// claims. A non-nullable string on a request record is a promise the compiler
/// believes and System.Text.Json does not keep: the property is simply left
/// null when the field is absent from the body, and every use downstream then
/// reads as safe when it is not. This one reached
/// <c>UserManager.CheckPasswordAsync</c> and came back as a 500.
/// </param>
public sealed record EnrolOtpRequest(string Destination, string? Password);

/// <param name="Password">See <see cref="EnrolOtpRequest.Password"/>.</param>
public sealed record StepUpRequest(string? Password);

/// <summary>
/// What a confirmed TOTP factor answers with.
/// </summary>
/// <param name="RecoveryCodes">
/// The ten codes, present ONLY when this confirmation created them — which is
/// the first time the account gets a second factor. Null afterwards, because
/// re-enrolling an authenticator must not silently invalidate codes the user
/// already wrote down.
/// </param>
public sealed record TotpConfirmedResponse(
    IReadOnlyList<string>? RecoveryCodes, string? Warning);

public sealed record FactorResponse(
    Guid Id,
    string Type,
    bool IsPrimary,
    bool Verified,
    string? MaskedDestination,
    int? RemainingCodes);

public sealed record TotpEnrolmentResponse(Guid FactorId, string Secret, string OtpAuthUri);

public static class MfaEndpoints
{
    public static IEndpointRouteBuilder MapMfaEndpoints(this IEndpointRouteBuilder app)
    {
        var anon = app.MapGroup("/api/v1/auth/mfa").WithTags("MFA");
        anon.MapPost("/verify", VerifyAsync).AllowAnonymous();
        anon.MapPost("/resend", ResendAsync).AllowAnonymous();
        anon.MapPost("/select", SelectFactorAsync).AllowAnonymous();

        var auth = app.MapGroup("/api/v1/auth/mfa").WithTags("MFA").RequireAuthorization();
        auth.MapGet("/factors", ListFactorsAsync);
        auth.MapPost("/totp/enrol", EnrolTotpAsync);
        auth.MapPost("/totp/confirm", ConfirmTotpAsync);
        auth.MapPost("/email/enrol", (EnrolOtpRequest r, HttpContext h, UserManager<AppUser> u,
                MfaService m, SecurityEventWriter e, CancellationToken ct)
            => EnrolOtpAsync(r, MfaFactorType.EmailOtp, h, u, m, e, ct));
        auth.MapPost("/otp/confirm", ConfirmOtpAsync);
        auth.MapPost("/recovery-codes", RegenerateRecoveryCodesAsync);
        auth.MapDelete("/factors/{factorId:guid}", RemoveFactorAsync);

        return app;
    }

    /// <summary>
    /// The second step of login. The challenge id grants nothing but the right to
    /// attempt this factor, for this user, for five minutes, a bounded number of
    /// times — it is not a token and must never be persisted by the client.
    /// </summary>
    private static async Task<IResult> VerifyAsync(
        MfaVerifyRequest request,
        MfaService mfa,
        AuthService auth,
        UserManager<AppUser> users,
        BruteForceGuard guard,
        SecurityEventWriter events,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var ip = ClientIp(http);
        var result = await mfa.VerifyChallengeAsync(request.ChallengeId, request.Code, ct);

        if (result.Failed)
        {
            await events.WriteAsync(SecurityEventType.MfaChallenge, succeeded: false,
                userId: null, ip, UserAgent(http),
                new { reason = result.Error.Code }, ct);

            var status = result.Error.Code == MfaErrors.AttemptsExhausted.Code
                ? StatusCodes.Status429TooManyRequests
                : StatusCodes.Status401Unauthorized;

            return Problem(result.Error, status, http);
        }

        var challenge = result.Value;
        var user = await users.FindByIdAsync(challenge.UserId.ToString());

        if (user is null || user.Status != UserStatus.Active)
            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status401Unauthorized, http);

        // A complete login: password AND second factor. Only now do the
        // brute-force counters reset.
        await guard.ResetAsync(AccountKey(user.Email!), ct);

        user.LastLoginAt = clock.UtcNow;
        await users.UpdateAsync(user);

        await events.WriteAsync(SecurityEventType.MfaChallenge, succeeded: true,
            user.Id, ip, UserAgent(http), new { factor = challenge.FactorType.ToString() }, ct);

        var pair = await auth.IssueAsync(user, ContextFrom(http), mfaSatisfied: true, ct);

        return Results.Ok(new TokenResponse(
            pair.AccessToken, pair.AccessExpiresAt, pair.RefreshToken, pair.RefreshExpiresAt));
    }

    /// <summary>
    /// Switches the in-flight challenge to a different enrolled factor — the
    /// "Başka bir yöntem seç" action on screen A04.
    ///
    /// Without this a challenge is stuck on the PRIMARY factor, and a recovery
    /// code entered at the TOTP prompt is verified as if it were a TOTP code and
    /// always fails. That is precisely the situation recovery codes exist for.
    /// </summary>
    private static async Task<IResult> SelectFactorAsync(
        MfaSelectRequest request,
        MfaService mfa,
        MfaChallengeStore challenges,
        HttpContext http,
        CancellationToken ct)
    {
        var challenge = await challenges.GetAsync(request.ChallengeId, ct);
        if (challenge is null)
            return Problem(MfaErrors.ChallengeExpired, StatusCodes.Status401Unauthorized, http);

        // The factor must belong to the user the ORIGINAL challenge was issued
        // for. The caller supplies a factor id but never a user id.
        var factors = await mfa.GetUsableFactorsAsync(challenge.UserId, ct);
        var factor = factors.FirstOrDefault(f => f.Id == request.FactorId);

        if (factor is null)
            return Problem(MfaErrors.NotEnrolled, StatusCodes.Status400BadRequest, http);

        // The old challenge dies, so switching cannot be used to multiply the
        // attempt budget.
        await challenges.ConsumeAsync(request.ChallengeId, ct);

        var newChallengeId = await mfa.StartChallengeAsync(challenge.UserId, factor, ct);

        return Results.Ok(new { challengeId = newChallengeId, factorType = factor.FactorType.ToString() });
    }

    private static async Task<IResult> ResendAsync(
        MfaResendRequest request,
        MfaService mfa,
        MfaChallengeStore challenges,
        HttpContext http,
        CancellationToken ct)
    {
        var challenge = await challenges.GetAsync(request.ChallengeId, ct);
        if (challenge is null)
            return Problem(MfaErrors.ChallengeExpired, StatusCodes.Status401Unauthorized, http);

        var factors = await mfa.GetUsableFactorsAsync(challenge.UserId, ct);
        var factor = factors.FirstOrDefault(f => f.Id == challenge.FactorId);
        if (factor is null)
            return Problem(MfaErrors.NotEnrolled, StatusCodes.Status400BadRequest, http);

        // The cooldown is enforced here, server-side. A client that ignores its
        // own countdown still gets refused.
        var newChallengeId = await mfa.StartChallengeAsync(challenge.UserId, factor, ct);

        return Results.Ok(new { challengeId = newChallengeId });
    }

    private static async Task<IResult> ListFactorsAsync(
        MfaService mfa, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var factors = await mfa.ListAsync(userId.Value, ct);

        return Results.Ok(factors.Select(f => new FactorResponse(
            f.Id, f.Type.ToString(), f.IsPrimary, f.Verified,
            f.MaskedDestination, f.RemainingCodes)));
    }

    /// <summary>
    /// Step-up: enrolling a factor re-confirms the password, so a hijacked
    /// session cannot quietly add an attacker's device.
    /// </summary>
    private static async Task<IResult> EnrolTotpAsync(
        StepUpRequest request,
        MfaService mfa,
        UserManager<AppUser> users,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await RequireStepUpAsync(request.Password, users, principal);
        if (user is null)
            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status403Forbidden, http);

        var enrolment = await mfa.BeginTotpEnrolmentAsync(user.Id, user.Email!, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, succeeded: true,
            user.Id, ClientIp(http), UserAgent(http), new { factor = "Totp", stage = "begin" }, ct);

        // The secret is returned ONCE, over TLS, and is never logged.
        return Results.Ok(new TotpEnrolmentResponse(
            enrolment.FactorId, enrolment.Secret, enrolment.OtpAuthUri));
    }

    private static async Task<IResult> ConfirmTotpAsync(
        ConfirmCodeRequest request,
        MfaService mfa,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var result = await mfa.ConfirmTotpAsync(userId.Value, request.Code, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, result.Succeeded,
            userId, ClientIp(http), UserAgent(http),
            new { factor = "Totp", stage = "confirm" }, ct);

        if (!result.Succeeded)
            return Problem(result.Error, StatusCodes.Status400BadRequest, http);

        // Recovery codes are issued HERE, with the factor, rather than being
        // offered afterwards as a separate step behind a second password
        // prompt.
        //
        // Offered, they get skipped — and an authenticator with no recovery
        // codes is a permanent lockout waiting for a lost phone, with no
        // operator path back by design. Acceptance testing locked two accounts
        // out of this dev database that way inside an hour.
        //
        // Only when the account has none. Re-enrolling an authenticator must
        // not quietly invalidate codes the user already wrote down, so an
        // account that already has usable codes keeps them and gets nulls here.
        var remaining = await mfa.CountRemainingCodesAsync(userId.Value, ct);

        if (remaining > 0) return Results.Ok(new TotpConfirmedResponse(null, null));

        var codes = await mfa.RegenerateRecoveryCodesAsync(userId.Value, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, succeeded: true,
            userId, ClientIp(http), UserAgent(http),
            new { factor = "RecoveryCode", count = codes.Count, stage = "auto" }, ct);

        return Results.Ok(new TotpConfirmedResponse(
            codes, "Bu kodlar bir daha gösterilmeyecek."));
    }

    private static async Task<IResult> EnrolOtpAsync(
        EnrolOtpRequest request,
        MfaFactorType type,
        HttpContext http,
        UserManager<AppUser> users,
        MfaService mfa,
        SecurityEventWriter events,
        CancellationToken ct)
    {
        var user = await RequireStepUpAsync(request.Password, users, http.User);
        if (user is null)
            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status403Forbidden, http);

        var factorId = await mfa.BeginOtpEnrolmentAsync(user.Id, type, request.Destination, ct);

        var factor = new MfaFactor
        {
            Id = factorId,
            UserId = user.Id,
            FactorType = type,
            Destination = request.Destination,
        };

        var challengeId = await mfa.StartChallengeAsync(user.Id, factor, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, succeeded: true,
            user.Id, ClientIp(http), UserAgent(http),
            new { factor = type.ToString(), stage = "begin" }, ct);

        return Results.Ok(new { factorId, challengeId });
    }

    private static async Task<IResult> ConfirmOtpAsync(
        MfaVerifyRequest request,
        MfaService mfa,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var result = await mfa.ConfirmOtpAsync(userId.Value, request.ChallengeId, request.Code, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, result.Succeeded,
            userId, ClientIp(http), UserAgent(http), new { stage = "confirm" }, ct);

        return result.Succeeded
            ? Results.NoContent()
            : Problem(result.Error, StatusCodes.Status400BadRequest, http);
    }

    /// <summary>Codes are returned ONCE. There is no endpoint that retrieves them again.</summary>
    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        StepUpRequest request,
        MfaService mfa,
        UserManager<AppUser> users,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await RequireStepUpAsync(request.Password, users, principal);
        if (user is null)
            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status403Forbidden, http);

        var codes = await mfa.RegenerateRecoveryCodesAsync(user.Id, ct);

        await events.WriteAsync(SecurityEventType.MfaEnrolled, succeeded: true,
            user.Id, ClientIp(http), UserAgent(http),
            new { factor = "RecoveryCode", count = codes.Count }, ct);

        return Results.Ok(new { codes, warning = "Bu kodlar bir daha gösterilmeyecek." });
    }

    private static async Task<IResult> RemoveFactorAsync(
        Guid factorId,
        MfaService mfa,
        UserManager<AppUser> users,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return Results.Unauthorized();

        var result = await mfa.RemoveFactorAsync(user.Id, factorId, user.MfaRequired, ct);

        await events.WriteAsync(SecurityEventType.MfaRemoved, result.Succeeded,
            user.Id, ClientIp(http), UserAgent(http), null, ct);

        return result.Succeeded
            ? Results.NoContent()
            : Problem(result.Error, StatusCodes.Status400BadRequest, http);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<AppUser?> RequireStepUpAsync(
        string? password, UserManager<AppUser> users, ClaimsPrincipal principal)
    {
        // An absent password is a refusal, not a crash. CheckPasswordAsync
        // throws ArgumentNullException on null, which surfaced as a 500 and told
        // the caller the server was broken when they had simply left a field
        // out. The answer here is the same 403 a WRONG password gets, so the
        // two stay indistinguishable.
        if (string.IsNullOrEmpty(password)) return null;

        var userId = UserId(principal);
        if (userId is null) return null;

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return null;

        return await users.CheckPasswordAsync(user, password) ? user : null;
    }

    internal static Guid? UserId(ClaimsPrincipal principal)
        => Guid.TryParse(principal.FindFirstValue("sub"), out var id) ? id : null;

    internal static string AccountKey(string email) => email.ToUpperInvariant();

    internal static string? ClientIp(HttpContext http)
        => http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf)
           && !string.IsNullOrWhiteSpace(cf)
            ? cf.ToString()
            : http.Connection.RemoteIpAddress?.ToString();

    internal static string UserAgent(HttpContext http)
        => http.Request.Headers.UserAgent.ToString();

    internal static AuthContext ContextFrom(HttpContext http)
        => new(ClientIp(http), UserAgent(http), DeviceId: null);

    internal static IResult Problem(Error error, int status, HttpContext http)
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
