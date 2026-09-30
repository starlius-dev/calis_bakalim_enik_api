using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Api.Features.Notifications;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Plan;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Plan;

public sealed record AgendaResponse(
    DateOnly From,
    DateOnly To,
    string TimeZone,
    IReadOnlyList<AgendaOccurrence> Occurrences,
    // One-off events (an exam, a presentation) in the window, J73. The week
    // page used to be built from the timetable alone and never showed them.
    IReadOnlyList<AgendaEvent>? Events = null);

/// <summary>An event on its local day; times are null when it is all-day.</summary>
public sealed record AgendaEvent(
    Guid Id,
    Guid? CourseId,
    DateOnly Date,
    TimeOnly? StartsAt,
    TimeOnly? EndsAt,
    DateTimeOffset StartsAtUtc,
    bool AllDay,
    string Type,
    string Title,
    string? Location);

public sealed record DashboardResponse(
    DateOnly Date,
    string TimeZone,
    string SkyVariant,
    IReadOnlyList<AgendaOccurrence> Schedule,
    IReadOnlyList<TaskResponse> Tasks,
    int DueToday,
    int Overdue,
    int UnreadNotifications,
    FocusSessionResponse? ActiveFocusSession);

/// <summary>
/// Bugün (01, D01) and Hafta (08, D06) — one request each.
/// </summary>
/// <remarks>
/// These exist so the two busiest screens are one round trip rather than five.
/// Everything in them is computed in the USER's time zone; the client never
/// decides what "today" means, because a travelling user's device would
/// disagree with their own schedule.
/// </remarks>
public static partial class PlanEndpointsViews
{
    internal static async Task<IResult> AgendaAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var zone = await ZoneAsync(db, userId.Value, ct);
        var today = Today(clock, zone);

        var start = from ?? today;
        var end = to ?? start.AddDays(6);

        if (end < start)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = ["Bitiş tarihi başlangıçtan önce olamaz."],
            });

        // Capped server-side: an unbounded range is a free way to burn CPU.
        // See docs/API-SURFACE.md §9.
        if (end.DayNumber - start.DayNumber >= AgendaExpander.MaxWindowDays)
            end = start.AddDays(AgendaExpander.MaxWindowDays - 1);

        var entries = await db.ScheduleEntries
            .Where(e => e.ValidFrom <= end && (e.ValidTo == null || e.ValidTo >= start))
            .ToListAsync(ct);

        // Events are stored as instants; the day and the wall-clock times are
        // the user's, like everything else on the page. Recurring events are
        // shown on their first date only: nothing creates them yet.
        var windowStart = UserDate.StartOfLocalDay(start, zone);
        var windowEnd = UserDate.EndOfLocalDay(end, zone);

        var events = await db.Events
            .Where(e => e.StartsAt >= windowStart && e.StartsAt < windowEnd)
            .OrderBy(e => e.StartsAt)
            .ToListAsync(ct);

        return Results.Ok(new AgendaResponse(
            start, end, zone.Id,
            AgendaExpander.Expand(entries, start, end, zone),
            events.Select(e => ToAgendaEvent(e, zone)).ToList()));
    }

    public static AgendaEvent ToAgendaEvent(Domain.Content.Event e, TimeZoneInfo zone)
    {
        var starts = TimeZoneInfo.ConvertTime(e.StartsAt, zone);
        var ends = e.EndsAt is { } end ? TimeZoneInfo.ConvertTime(end, zone) : (DateTimeOffset?)null;

        return new AgendaEvent(
            e.Id,
            e.CourseId,
            DateOnly.FromDateTime(starts.DateTime),
            e.AllDay ? null : TimeOnly.FromDateTime(starts.DateTime),
            e.AllDay || ends is null ? null : TimeOnly.FromDateTime(ends.Value.DateTime),
            e.StartsAt,
            e.AllDay,
            e.EventType.ToString(),
            e.Title,
            e.Location);
    }

    internal static async Task<IResult> DashboardAsync(
        AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var zone = await ZoneAsync(db, userId.Value, ct);
        var now = clock.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(localNow.DateTime);

        var endOfToday = EndOfLocalDay(today, zone);
        var startOfToday = StartOfLocalDay(today, zone);

        var entries = await db.ScheduleEntries
            .Where(e => e.ValidFrom <= today && (e.ValidTo == null || e.ValidTo >= today))
            .ToListAsync(ct);

        var tasks = await db.Tasks
            .Where(t => t.CompletedAt == null)
            .OrderBy(t => t.DueAt == null)
            .ThenBy(t => t.DueAt)
            .ThenByDescending(t => t.Priority)
            .Take(5)
            .ToListAsync(ct);

        var dueToday = await db.Tasks.CountAsync(
            t => t.CompletedAt == null && t.DueAt >= startOfToday && t.DueAt <= endOfToday, ct);

        var overdue = await db.Tasks.CountAsync(
            t => t.CompletedAt == null && t.DueAt != null && t.DueAt < startOfToday, ct);

        // The same "has arrived" rule the inbox uses, not every unread row
        // (J64). Reminders are written ahead with a future ScheduledAt; counting
        // them put two weeks of doses that had not happened yet on the home
        // badge, while the inbox it opens showed almost nothing.
        var unread = await NotificationEndpoints
            .Due(db, userId.Value, now)
            .CountAsync(n => n.ReadAt == null, ct);

        // A session started and never ended — the timer the user left running,
        // closed first if it passed the 24-hour cap (J76).
        var active = await db.FocusSessions
            .Where(s => s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (active is not null && FocusRules.ExpireIfOverCap(active, now))
        {
            await db.SaveChangesAsync(ct);
            active = null;
        }

        return Results.Ok(new DashboardResponse(
            today,
            zone.Id,
            SkyVariant(localNow.Hour),
            AgendaExpander.Expand(entries, today, today, zone),
            tasks.Select(t => new TaskResponse(
                t.Id, t.Title, t.Notes, t.Status.ToString(), t.Priority.ToString(),
                t.DueAt, t.ReminderAt, t.CompletedAt, t.ParentTaskId,
                t.CourseId, t.ProjectId, t.CreatedAt)).ToList(),
            dueToday,
            overdue,
            unread,
            active is null ? null : PlanEndpoints.DescribeFocus(active, now)));
    }

    /// <summary>
    /// Which pixel-sky the banner draws, from the user's local hour.
    /// </summary>
    /// <remarks>
    /// Decided here, not on the device: a user in another time zone would
    /// otherwise see a night sky above a morning schedule. Matches
    /// SkyVariant.fromHour in the Flutter client — the two must agree.
    /// </remarks>
    private static string SkyVariant(int localHour) => localHour switch
    {
        >= 5 and < 9 => "dawn",
        >= 9 and < 17 => "day",
        >= 17 and < 21 => "dusk",
        _ => "night",
    };

    // Zone and local-day arithmetic lives in UserDate. It used to be copied
    // here, and the copies in the Health endpoints were the ones that drifted
    // into using the UTC date.
    private static Task<TimeZoneInfo> ZoneAsync(
        AppDbContext db, Guid userId, CancellationToken ct) =>
        UserDate.ZoneAsync(db, userId, ct);

    private static DateOnly Today(IClock clock, TimeZoneInfo zone) =>
        UserDate.Today(clock.UtcNow, zone);

    private static DateTimeOffset StartOfLocalDay(DateOnly day, TimeZoneInfo zone) =>
        UserDate.StartOfLocalDay(day, zone);

    private static DateTimeOffset EndOfLocalDay(DateOnly day, TimeZoneInfo zone) =>
        UserDate.EndOfLocalDay(day, zone);
}
