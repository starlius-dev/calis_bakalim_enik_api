using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
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
    IReadOnlyList<AgendaOccurrence> Occurrences);

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

        return Results.Ok(new AgendaResponse(
            start, end, zone.Id, AgendaExpander.Expand(entries, start, end, zone)));
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

        var unread = await db.Notifications.CountAsync(
            n => n.UserId == userId && n.ReadAt == null, ct);

        // A session started and never ended — the timer the user left running.
        var active = await db.FocusSessions
            .Where(s => s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

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
            active is null
                ? null
                : new FocusSessionResponse(
                    active.Id, active.TaskId, active.StartedAt, active.EndedAt,
                    active.PlannedBlocks, active.DoneBlocks, active.FocusSeconds)));
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
