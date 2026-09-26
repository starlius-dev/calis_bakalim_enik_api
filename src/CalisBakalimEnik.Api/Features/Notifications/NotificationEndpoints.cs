using System.Security.Claims;
using System.Text.Json;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Notifications;

public sealed record NotificationResponse(
    Guid Id,
    string Type,
    string Title,
    string Body,
    string? Route,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);

public sealed record PreferenceRequest(
    string Type,
    bool Push,
    bool Email,
    bool InApp,
    TimeOnly? QuietFrom,
    TimeOnly? QuietTo);

public sealed record PreferenceResponse(
    string Type,
    bool Push,
    bool Email,
    bool InApp,
    TimeOnly? QuietFrom,
    TimeOnly? QuietTo);

/// <summary>
/// The in-app inbox, its unread count and the per-type preferences.
/// </summary>
/// <remarks>
/// Every query filters on the caller's own id. These tables carry a UserId
/// rather than an OwnerId — the outbox processor has no request context — so
/// the global ownership filter does not apply and the scoping is explicit
/// here. A missing <c>Where(UserId)</c> in this file is a data leak.
/// </remarks>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/unread-count", UnreadCountAsync);
        group.MapPost("/{id:guid}/read", MarkReadAsync);
        group.MapPost("/read-all", MarkAllReadAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        var preferences = app.MapGroup("/api/v1/notification-preferences")
            .WithTags("Notifications")
            .RequireAuthorization();

        preferences.MapGet("/", ListPreferencesAsync);
        preferences.MapPut("/", UpsertPreferenceAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct,
        int take = 30,
        DateTimeOffset? before = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Keyset pagination on created_at, which is exactly what
        // ix_notifications_inbox is ordered for. OFFSET would degrade as the
        // inbox grows and this is the screen opened most often.
        var query = db.Notifications.Where(n => n.UserId == userId);
        if (before is not null) query = query.Where(n => n.CreatedAt < before);

        var page = await query
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(take, 1, 100))
            .Select(n => new
            {
                n.Id,
                n.Type,
                n.Title,
                n.Body,
                n.Data,
                n.CreatedAt,
                n.ReadAt,
            })
            .ToListAsync(ct);

        // The route is unpacked here rather than in SQL: it is one small json
        // field per row on an already-paged result, and a jsonb accessor in the
        // projection buys nothing but a harder query to read.
        return Results.Ok(page.Select(n => new NotificationResponse(
            n.Id,
            n.Type.ToString(),
            n.Title,
            n.Body,
            RouteOf(n.Data),
            n.CreatedAt,
            n.ReadAt)));
    }

    /// <summary>
    /// Pulls `route` out of the stored data blob.
    /// </summary>
    /// <remarks>
    /// The server decides where a tap lands; the client only navigates there.
    /// A malformed blob yields null rather than throwing — one bad row must not
    /// take down the whole inbox.
    /// </remarks>
    private static string? RouteOf(string data)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(data);
            return parsed is not null && parsed.TryGetValue("route", out var route)
                ? route
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IResult> UnreadCountAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // The badge comes from here rather than from local counting, so the
        // same account on two devices agrees. See docs/NOTIFICATIONS.md §8.
        var count = await db.Notifications
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);

        return Results.Ok(new { count });
    }

    /// <summary>
    /// Removes one notification from the inbox.
    /// </summary>
    /// <remarks>
    /// <para>The client has had a <c>Dismissible</c> on every inbox row since it
    /// was written, calling <c>DELETE /notifications/{id}</c>. Nothing answered
    /// it: the row vanished optimistically, the request 404'd, and the
    /// notification came back on the next refresh. An acceptance test found it;
    /// no unit test could have, because both halves were individually
    /// reasonable.</para>
    ///
    /// <para><b>A hard delete, deliberately.</b> An inbox row is not an audit
    /// record — <c>security_events</c> is, and it is a different table with its
    /// own retention. The retention sweep already hard-deletes read
    /// notifications and relies on the cascade into
    /// <c>notification_deliveries</c>, so doing anything else here would leave
    /// two different meanings of "deleted" in one table. It also avoids a
    /// migration to add a column whose only reader would be a query filter.</para>
    /// </remarks>
    private static async Task<IResult> DeleteAsync(
        Guid id,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Scoped by UserId for the same reason MarkReadAsync is: otherwise any
        // authenticated caller could delete someone else's row by guessing an
        // id. 404 rather than 403 — it is not theirs to learn the existence of.
        var removed = await db.Notifications
            .Where(n => n.Id == id && n.UserId == userId)
            .ExecuteDeleteAsync(ct);

        return removed == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> MarkReadAsync(
        Guid id,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Scoped by UserId as well as id: without it any authenticated user
        // could mark someone else's notification read by guessing an id.
        var updated = await db.Notifications
            .Where(n => n.Id == id && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, clock.UtcNow), ct);

        // 404 rather than 403 for someone else's row — it is not theirs to
        // learn the existence of. See docs/SECURITY.md §7.
        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> MarkAllReadAsync(
        AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var updated = await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, clock.UtcNow), ct);

        return Results.Ok(new { updated });
    }

    private static async Task<IResult> ListPreferencesAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var stored = await db.NotificationPreferences
            .Where(p => p.UserId == userId)
            .ToListAsync(ct);

        // Every type is returned, stored or not: a settings screen should show
        // the defaults it will get, not an empty list that implies "off".
        var all = Enum.GetValues<NotificationType>().Select(type =>
        {
            var preference = stored.FirstOrDefault(p => p.Type == type);

            return new PreferenceResponse(
                type.ToString(),
                preference?.Push ?? type != NotificationType.System,
                preference?.Email ?? type is NotificationType.ProjectDeadline
                    or NotificationType.SecurityAlert,
                preference?.InApp ?? true,
                preference?.QuietFrom,
                preference?.QuietTo);
        });

        return Results.Ok(all);
    }

    private static async Task<IResult> UpsertPreferenceAsync(
        PreferenceRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<NotificationType>(request.Type, out var type))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["type"] = ["Bilinmeyen bildirim türü."],
            });

        var preference = await db.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == userId && p.Type == type, ct);

        if (preference is null)
        {
            preference = new NotificationPreference { UserId = userId.Value, Type = type };
            db.NotificationPreferences.Add(preference);
        }

        preference.Push = request.Push;
        preference.Email = request.Email;
        preference.InApp = request.InApp;
        preference.QuietFrom = request.QuietFrom;
        preference.QuietTo = request.QuietTo;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
