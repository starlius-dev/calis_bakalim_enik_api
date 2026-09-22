using System.Security.Claims;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Features.Auth;

public sealed record RegisterRequest(string Email, string Password, string DisplayName);
public sealed record ConfirmEmailRequest(string UserId, string Token);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);

/// <summary>Returned by /login when a second factor is enrolled.</summary>
public sealed record MfaRequiredResponse(
    bool MfaRequired,
    Guid ChallengeId,
    IReadOnlyCollection<FactorResponse> Factors);

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

public sealed record UpdateMeRequest(
    string? DisplayName,
    string? Locale,
    string? TimeZone);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        // The budgets are declared next to the routes on purpose: a new
        // endpoint cannot silently inherit, or miss, someone else's limit.
        // See docs/SECURITY.md §5, layer 3.
        group.MapPost("/register", RegisterAsync)
            .AllowAnonymous()
            .RateLimit(RateLimitGuard.Policies.Register);

        group.MapPost("/confirm-email", ConfirmEmailAsync).AllowAnonymous();

        group.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .RateLimit(RateLimitGuard.Policies.Login);

        group.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .RateLimit(RateLimitGuard.Policies.Refresh);
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapPost("/logout-all", LogoutAllAsync).RequireAuthorization();
        group.MapGet("/me", MeAsync).RequireAuthorization();
        group.MapPatch("/me", UpdateMeAsync).RequireAuthorization();

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
        IOptions<EmailOptions> links,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Auth.Register");
        var existing = await users.FindByEmailAsync(request.Email);

        if (existing is not null)
        {
            await SendQuietlyAsync(email, logger, request.Email,
                "Çalış Bakalım Enik — kayıt denemesi",
                $"""
                 Bu adresle zaten bir hesap var, bu yüzden yeni bir hesap açmadık.

                 Şifreni hatırlamıyorsan buradan sıfırlayabilirsin:

                 {EmailLinks.ForgotPassword(links.Value)}
                 """,
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

        await SendQuietlyAsync(email, logger, request.Email,
            "Çalış Bakalım Enik — e-postanı doğrula",
            $"""
             Merhaba {user.DisplayName},

             Hesabını açmak için son bir adım kaldı. Aşağıdaki bağlantıya tıkla:

             {EmailLinks.ConfirmEmail(links.Value, user.Id, token)}

             Bağlantı 24 saat geçerli.
             """,
            ct);

        return Results.NoContent();
    }

    /// <summary>
    /// Sends without letting a delivery failure change the response.
    ///
    /// Registration answers 204 whether or not the address exists. If a provider
    /// outage turned one branch into a 500 while the other stayed 204, the
    /// enumeration oracle this flow is built to avoid would be back — visible
    /// exactly when nobody is watching. The failure goes to the log instead.
    /// </summary>
    private static async Task SendQuietlyAsync(
        IEmailSender email,
        ILogger logger,
        string to,
        string subject,
        string body,
        CancellationToken ct)
    {
        try
        {
            await email.SendAsync(to, subject, body, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Email delivery failed. Subject={Subject}", subject);
        }
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        UserManager<AppUser> users,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(request.UserId);

        // A dead link and an unknown user answer IDENTICALLY, so this still
        // reveals nothing about which addresses exist — but a real person whose
        // link expired now learns that, instead of seeing "confirmed" and then
        // being unable to sign in.
        if (user is null)
            return Problem(AuthErrors.InvalidConfirmationToken, StatusCodes.Status400BadRequest, http);

        var result = await users.ConfirmEmailAsync(user, request.Token);
        if (!result.Succeeded)
            return Problem(AuthErrors.InvalidConfirmationToken, StatusCodes.Status400BadRequest, http);

        user.Status = UserStatus.Active;
        await users.UpdateAsync(user);

        return Results.NoContent();
    }

    /// <summary>
    /// Password step. A wrong password, an unknown address and a disabled account
    /// all return the same body, and the unknown-user branch still hashes a dummy
    /// password so the timing does not reveal whether the address exists.
    ///
    /// When MFA is enrolled this returns a CHALLENGE, not tokens.
    /// </summary>
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserManager<AppUser> users,
        AuthService auth,
        MfaService mfa,
        BruteForceGuard guard,
        SecurityEventWriter events,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var ip = MfaEndpoints.ClientIp(http);
        var agent = MfaEndpoints.UserAgent(http);
        var accountKey = MfaEndpoints.AccountKey(request.Email);

        // Layer 1 and 2, checked BEFORE any password work: a locked account must
        // not be a free PBKDF2 oracle.
        var lockout = await guard.CheckAsync(accountKey, ip, ct);
        if (lockout.IsLocked) return Locked(lockout, http);

        var user = await users.FindByEmailAsync(request.Email);

        if (user is null)
        {
            // Burn equivalent CPU so the response time does not reveal whether
            // the address exists. The result is deliberately discarded.
            _ = users.PasswordHasher.VerifyHashedPassword(
                new AppUser(), DummyHashFor(users.PasswordHasher), request.Password);

            var unknownState = await guard.RecordFailureAsync(accountKey, ip, ct);

            await events.WriteAsync(SecurityEventType.LoginAttempt, succeeded: false,
                userId: null, ip, agent, new { reason = "unknown_user" }, ct);

            // Record the lockout even though there is no user to attach it to —
            // security_events.user_id is nullable for exactly this case, and a
            // password-spray campaign is only visible if these are kept.
            if (unknownState.IsLocked)
            {
                await events.WriteAsync(SecurityEventType.AccountLocked, succeeded: true,
                    userId: null, ip, agent,
                    new { retryAfter = unknownState.RetryAfter?.TotalSeconds, target = "unknown_user" },
                    ct);
            }

            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status401Unauthorized, http);
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            var state = await guard.RecordFailureAsync(accountKey, ip, ct);

            await events.WriteAsync(SecurityEventType.LoginAttempt, succeeded: false,
                user.Id, ip, agent, new { reason = "bad_password" }, ct);

            if (state.IsLocked)
            {
                await events.WriteAsync(SecurityEventType.AccountLocked, succeeded: true,
                    user.Id, ip, agent, new { retryAfter = state.RetryAfter?.TotalSeconds }, ct);

                return Locked(state, http);
            }

            return Problem(AuthErrors.InvalidCredentials, StatusCodes.Status401Unauthorized, http);
        }

        if (user.Status == UserStatus.PendingConfirmation)
            return Problem(AuthErrors.NotConfirmed, StatusCodes.Status403Forbidden, http);

        if (user.Status == UserStatus.Disabled)
            return Problem(AuthErrors.Disabled, StatusCodes.Status403Forbidden, http);

        // ── second factor ────────────────────────────────────────────────
        var factors = await mfa.GetUsableFactorsAsync(user.Id, ct);

        if (factors.Count > 0 || user.MfaRequired)
        {
            if (factors.Count == 0)
                return Problem(MfaErrors.NotEnrolled, StatusCodes.Status403Forbidden, http);

            var primary = factors.First();
            var challengeId = await mfa.StartChallengeAsync(user.Id, primary, ct);
            var remainingCodes = await mfa.CountRemainingCodesAsync(user.Id, ct);

            // The counters are NOT reset here: the login is not complete until
            // the second factor succeeds.
            return Results.Ok(new MfaRequiredResponse(
                true,
                challengeId,
                factors.Select(f => new FactorResponse(
                    f.Id,
                    f.FactorType.ToString(),
                    f.IsPrimary,
                    Verified: true,
                    f.MaskedDestination,
                    RemainingCodes: f.FactorType == MfaFactorType.RecoveryCode
                        ? remainingCodes
                        : null)).ToArray()));
        }

        await guard.ResetAsync(accountKey, ct);

        user.LastLoginAt = clock.UtcNow;
        await users.UpdateAsync(user);

        await events.WriteAsync(SecurityEventType.LoginAttempt, succeeded: true,
            user.Id, ip, agent, new { mfa = false }, ct);

        var pair = await auth.IssueAsync(user, ContextFrom(http), mfaSatisfied: false, ct);

        return Results.Ok(ToResponse(pair));
    }

    private static IResult Locked(LockoutState state, HttpContext http)
    {
        var seconds = (int)Math.Ceiling(state.RetryAfter?.TotalSeconds ?? 60);
        http.Response.Headers.RetryAfter = seconds.ToString();

        return Problem(
            new Application.Common.Models.Error(
                "auth.locked",
                $"Çok fazla deneme. {seconds / 60} dk {seconds % 60} sn sonra tekrar dene."),
            StatusCodes.Status423Locked,
            http);
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
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        await auth.RevokeAllForUserAsync(userId.Value, RefreshRevokedReason.Logout, ct);
        await DenylistCurrentAccessTokenAsync(tokens, principal, ct);

        // The denylist only reaches the token in this request. Every OTHER
        // device is holding an access token whose jti nothing recorded, and
        // "sign out everywhere" that leaves those working for fifteen minutes
        // is not what the button says — least of all when it is pressed
        // because someone believes their account is compromised.
        await tokens.RevokeIssuedBeforeAsync(userId.Value, clock.UtcNow, ct);

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

    /// <summary>
    /// Profil — the fields a user owns about themselves.
    /// </summary>
    /// <remarks>
    /// The time zone matters more than it looks. Every "today" in the app is
    /// computed from it server-side — the day a meal is filed under, the dose
    /// horizon, which bar a focus session lands on — so until this existed a
    /// user who travelled or whose account guessed wrong had no way to correct
    /// it. See <see cref="Infrastructure.Identity.UserDate"/>.
    ///
    /// Email is NOT here: changing it is a security operation that needs
    /// confirmation on both addresses, and it belongs with the other
    /// re-authenticated actions rather than in a profile form.
    /// </remarks>
    private static async Task<IResult> UpdateMeAsync(
        UpdateMeRequest request,
        UserManager<AppUser> users,
        ICurrentUser currentUser,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return Results.Unauthorized();

        if (request.DisplayName is { } name)
        {
            var trimmed = name.Trim();

            if (trimmed.Length is < 2 or > 100)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["displayName"] = ["Ad 2 ile 100 karakter arasında olmalı."],
                });
            }

            user.DisplayName = trimmed;
        }

        if (request.Locale is { } locale)
        {
            if (locale is not ("tr" or "en"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["locale"] = ["Desteklenen diller: tr, en."],
                });
            }

            user.Locale = locale;
        }

        if (request.TimeZone is { } zone)
        {
            // Validated against the real database, not a regex: an unknown id
            // would be silently resolved to the app default on every read, and
            // the user would see their day quietly computed in someone else's
            // zone with nothing to explain it.
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["timeZone"] = ["Geçerli bir saat dilimi seç."],
                });
            }

            user.TimeZone = zone;
        }

        var result = await users.UpdateAsync(user);
        if (!result.Succeeded)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["profile"] = [string.Join(" ", result.Errors.Select(e => e.Description))],
            });
        }

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

    /// <summary>
    /// A REAL PBKDF2 hash, produced once by the configured hasher, so the
    /// unknown-user branch does equivalent work to a genuine verification.
    ///
    /// It must not be a hand-written constant: a string that is not valid base64
    /// makes VerifyHashedPassword throw, turning the timing mitigation into a
    /// 500 — which is a louder oracle than the timing difference it was meant to
    /// hide. Computed lazily so the cost lands once, not per request.
    /// </summary>
    private static string? _dummyHash;

    private static string DummyHashFor(IPasswordHasher<AppUser> hasher)
        => _dummyHash ??= hasher.HashPassword(new AppUser(), "not-a-real-password");

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
