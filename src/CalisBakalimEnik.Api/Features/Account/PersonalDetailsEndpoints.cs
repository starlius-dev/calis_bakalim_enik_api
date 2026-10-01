using System.Net.Mail;
using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Api.Features.Account;

/// <param name="DisplayName">The new name, or null to leave it.</param>
/// <param name="Email">The new address, or null to leave it. Changes only once
/// the link sent to it is opened.</param>
public sealed record UpdateDetailsRequest(string? DisplayName, string? Email);

/// <param name="PendingEmail">The address a confirmation link was just sent
/// to, echoed back so the app can say where to look.</param>
public sealed record DetailsResponse(string DisplayName, string Email, string? PendingEmail);

public sealed record ConfirmEmailChangeRequest(Guid UserId, string Email, string Token);

public sealed record DetailChangeResponse(
    string Field, string? OldValue, string? NewValue, DateTimeOffset ChangedAt);

/// <summary>
/// Changing the name and e-mail address (J78).
/// </summary>
/// <remarks>
/// <para>Both sit behind the same step-up as export and deletion (password,
/// then the second factor), with its own purpose, so a stolen session alone
/// cannot move the account to another address.</para>
///
/// <para>A new address has to be confirmed from that address before it
/// replaces the old one, and the old one is told at both steps. Every
/// previous value is kept in <c>personal_detail_changes</c> for twelve months
/// (decided 30 Sep 2026) to undo a takeover, then swept by RetentionCleanup.</para>
/// </remarks>
public static class PersonalDetailsEndpoints
{
    public const string NameField = "displayName";
    public const string EmailField = "email";

    public static IEndpointRouteBuilder MapPersonalDetailsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/account").WithTags("Account");

        group.MapPatch("/details", UpdateAsync).RequireAuthorization();
        group.MapGet("/details/history", HistoryAsync).RequireAuthorization();

        // Anonymous: the link can be opened on a device that is not signed
        // in. The token is the credential, and it is bound to the user and
        // the exact new address.
        group.MapPost("/email-change/confirm", ConfirmEmailAsync).AllowAnonymous();

        return app;
    }

    /// <summary>The name rule registration and the profile already use.</summary>
    public static string? CheckName(string name) =>
        name.Trim().Length is < 2 or > 100 ? "Ad 2 ile 100 karakter arasında olmalı." : null;

    /// <summary>A plain address, nothing MailAddress would quietly rewrite.</summary>
    public static bool IsEmail(string email) =>
        email.Length <= 256
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email
        && email.Contains('.', StringComparison.Ordinal);

    private static async Task<IResult> UpdateAsync(
        UpdateDetailsRequest request,
        UserManager<AppUser> users,
        AppDbContext db,
        StepUpTokens stepUps,
        SecurityEventWriter events,
        IEmailSender email,
        IOptions<EmailOptions> links,
        IClock clock,
        ILoggerFactory loggers,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var user = await users.FindByIdAsync(userId.Value.ToString());
        if (user is null) return Results.Unauthorized();

        // Validated BEFORE the step-up token is spent: a typo must not cost
        // the person their password and code again.
        var name = request.DisplayName?.Trim();
        var newEmail = request.Email?.Trim();

        var errors = new Dictionary<string, string[]>();
        if (name is not null && CheckName(name) is { } nameError)
            errors["displayName"] = [nameError];
        if (newEmail is not null && !IsEmail(newEmail))
            errors["email"] = ["Geçerli bir e-posta adresi gir."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var nameChanges = name is not null && name != user.DisplayName;
        var emailChanges = newEmail is not null
            && !string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase);

        if (!nameChanges && !emailChanges)
            return Results.Ok(new DetailsResponse(user.DisplayName, user.Email!, null));

        if (!await stepUps.ConsumeAsync(http.Request.Headers[AccountDataEndpoints.StepUpHeader],
                user.Id, StepUpTokens.Details, ct))
            return StepUpRequired(http);

        var now = clock.UtcNow;
        var ip = MfaEndpoints.ClientIp(http);
        var agent = MfaEndpoints.UserAgent(http);

        if (nameChanges)
        {
            db.PersonalDetailChanges.Add(new PersonalDetailChange
            {
                UserId = user.Id,
                Field = NameField,
                OldValue = user.DisplayName,
                NewValue = name,
                ChangedAt = now,
            });
            await db.SaveChangesAsync(ct);

            user.DisplayName = name!;
            await users.UpdateAsync(user);

            await events.WriteAsync(SecurityEventType.DisplayNameChanged, succeeded: true,
                user.Id, ip, agent, null, ct);
        }

        if (emailChanges)
        {
            var logger = loggers.CreateLogger("Account.Details");

            if (await users.FindByEmailAsync(newEmail!) is { } owner)
            {
                // The app hears the same as for a free address; the new
                // address is told why nothing happened, in its owner's language.
                await SendQuietlyAsync(email, logger, newEmail,
                    PersonalDetailEmails.AlreadyUsed(owner.Locale), ct);
            }
            else
            {
                var token = await users.GenerateChangeEmailTokenAsync(user, newEmail!);
                var link = EmailLinks.ChangeEmail(links.Value, user.Id, newEmail!, token);

                await SendQuietlyAsync(email, logger, newEmail,
                    PersonalDetailEmails.Confirm(user.Locale, user.DisplayName, link), ct);
            }

            await SendQuietlyAsync(email, logger, user.Email,
                PersonalDetailEmails.Requested(user.Locale, user.DisplayName, newEmail!), ct);

            await events.WriteAsync(SecurityEventType.EmailChangeRequested, succeeded: true,
                user.Id, ip, agent, null, ct);
        }

        return Results.Ok(new DetailsResponse(
            user.DisplayName, user.Email!, emailChanges ? newEmail : null));
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailChangeRequest request,
        UserManager<AppUser> users,
        AppDbContext db,
        SecurityEventWriter events,
        IEmailSender email,
        IClock clock,
        ILoggerFactory loggers,
        HttpContext http,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(request.UserId.ToString());
        var newEmail = request.Email?.Trim() ?? string.Empty;

        // Unknown user, bad token and expired token answer alike.
        if (user is null || !IsEmail(newEmail) || string.IsNullOrEmpty(request.Token))
            return BadLink(http);

        // Opening the link twice is not an error.
        if (string.Equals(user.Email, newEmail, StringComparison.OrdinalIgnoreCase))
            return Results.NoContent();

        var oldEmail = user.Email;
        var changed = await users.ChangeEmailAsync(user, newEmail, request.Token);

        if (!changed.Succeeded)
        {
            await events.WriteAsync(SecurityEventType.EmailChanged, succeeded: false,
                user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http),
                new { reason = changed.Errors.FirstOrDefault()?.Code }, ct);
            return BadLink(http);
        }

        // The e-mail address is also the sign-in name.
        await users.SetUserNameAsync(user, newEmail);

        db.PersonalDetailChanges.Add(new PersonalDetailChange
        {
            UserId = user.Id,
            Field = EmailField,
            OldValue = oldEmail,
            NewValue = newEmail,
            ChangedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);

        await events.WriteAsync(SecurityEventType.EmailChanged, succeeded: true,
            user.Id, MfaEndpoints.ClientIp(http), MfaEndpoints.UserAgent(http), null, ct);

        await SendQuietlyAsync(email, loggers.CreateLogger("Account.Details"), oldEmail,
            PersonalDetailEmails.Changed(user.Locale, user.DisplayName, newEmail), ct);

        return Results.NoContent();
    }

    private static async Task<IResult> HistoryAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var rows = await db.PersonalDetailChanges
            .Where(c => c.UserId == userId.Value)
            .OrderByDescending(c => c.ChangedAt)
            .Take(100)
            .Select(c => new DetailChangeResponse(c.Field, c.OldValue, c.NewValue, c.ChangedAt))
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static IResult BadLink(HttpContext http) =>
        MfaEndpoints.Problem(
            new Error("account.email_change_link", "Bağlantı geçersiz ya da süresi dolmuş."),
            StatusCodes.Status400BadRequest,
            http);

    private static IResult StepUpRequired(HttpContext http) =>
        MfaEndpoints.Problem(
            new Error("account.step_up_required", "Bu işlem için şifreni yeniden girmen gerekiyor."),
            StatusCodes.Status403Forbidden,
            http);

    /// <summary>A failed mail must not undo or fail the request itself.</summary>
    private static async Task SendQuietlyAsync(
        IEmailSender email, ILogger logger, string? to, MailText mail,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        var subject = mail.Subject;

        try
        {
            await email.SendAsync(to, mail.Subject, mail.Body, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Mail '{Subject}' could not be sent.", subject);
        }
    }
}
