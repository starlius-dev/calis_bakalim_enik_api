using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Plan;

/// <summary>One bar of the weekly chart.</summary>
public sealed record DayValue(DateOnly Date, int Value);

public sealed record FocusStats(
    int TotalMinutes,
    int Sessions,
    int BlocksDone,
    IReadOnlyList<DayValue> ByDay);

public sealed record TaskStats(
    int Completed,
    int Created,
    int CompletionPct,
    int OpenOverdue,
    IReadOnlyList<DayValue> CompletedByDay);

public sealed record StreakStats(
    IReadOnlyList<bool> Days,
    int Current,
    int Longest);

public sealed record WorkoutStats(
    int Sessions,
    int TotalMinutes,
    int TotalSets,
    int CaloriesKcal);

public sealed record NutritionStats(
    int DaysLogged,
    int AverageKcal,
    decimal? WeightKg,
    decimal? WeightChangeKg);

public sealed record MedicationStats(int AdherencePct, int Taken, int Resolved);

public sealed record StatsResponse(
    DateOnly From,
    DateOnly To,
    string TimeZone,
    FocusStats Focus,
    TaskStats Tasks,
    StreakStats Streak,
    WorkoutStats Workouts,
    NutritionStats Nutrition,
    MedicationStats Medication);

/// <summary>
/// İstatistikler (13, D10) and Sağlık istatistikleri (27) — one request.
/// </summary>
/// <remarks>
/// Both screens are read-only summaries over the same window, so they share an
/// endpoint rather than making a phone fire eight queries to draw two charts.
///
/// Every bucket is a day in the USER's zone. Grouping by the UTC day would put
/// a 01:00 focus session on the wrong bar for anyone east of UTC, and the chart
/// would quietly disagree with the screen the session was started from.
/// </remarks>
public static class StatsEndpoints
{
    /// <summary>The 28 cells the design's streak grid draws.</summary>
    private const int StreakDays = 28;

    /// <summary>
    /// The widest window accepted. The same reasoning as /agenda's cap: an
    /// unbounded range is a way to spend the server's CPU for free.
    /// </summary>
    private const int MaxWindowDays = 366;

    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/stats", GetAsync)
            .WithTags("Plan")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> GetAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateOnly? from = null,
        DateOnly? to = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var zone = await UserDate.ZoneAsync(db, userId.Value, ct);
        var today = UserDate.Today(clock.UtcNow, zone);

        // The default is the last seven days INCLUDING today, which is what the
        // weekly bar chart draws.
        var end = to ?? today;
        var start = from ?? end.AddDays(-6);

        if (end < start)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = ["Bitiş tarihi başlangıçtan önce olamaz."],
            });
        }

        if (end.DayNumber - start.DayNumber >= MaxWindowDays)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = [$"Aralık en fazla {MaxWindowDays} gün olabilir."],
            });
        }

        var windowStart = UserDate.StartOfLocalDay(start, zone);
        var windowEnd = UserDate.EndOfLocalDay(end, zone);

        var focus = await FocusAsync(db, zone, start, end, windowStart, windowEnd, ct);
        var tasks = await TasksAsync(db, clock, zone, start, end, windowStart, windowEnd, ct);
        var streak = await StreakAsync(db, zone, today, ct);
        var workouts = await WorkoutsAsync(db, windowStart, windowEnd, ct);
        var nutrition = await NutritionAsync(db, start, end, ct);
        var medication = await MedicationAsync(db, windowStart, windowEnd, ct);

        return Results.Ok(new StatsResponse(
            start, end, zone.Id, focus, tasks, streak, workouts, nutrition, medication));
    }

    private static async Task<FocusStats> FocusAsync(
        AppDbContext db,
        TimeZoneInfo zone,
        DateOnly start,
        DateOnly end,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        var sessions = await db.FocusSessions
            .Where(f => f.StartedAt >= windowStart && f.StartedAt < windowEnd)
            .Select(f => new { f.StartedAt, f.FocusSeconds, f.DoneBlocks })
            .ToListAsync(ct);

        // FocusSeconds, not the gap between start and end: a paused timer is not
        // wall-clock elapsed, and counting it as study would flatter every day
        // that had a long coffee break in it.
        var byDay = sessions
            .GroupBy(s => UserDate.LocalDayOf(s.StartedAt, zone))
            .ToDictionary(g => g.Key, g => g.Sum(s => s.FocusSeconds));

        return new FocusStats(
            sessions.Sum(s => s.FocusSeconds) / 60,
            sessions.Count,
            sessions.Sum(s => (int)s.DoneBlocks),
            Series(start, end, day => (byDay.GetValueOrDefault(day)) / 60));
    }

    private static async Task<TaskStats> TasksAsync(
        AppDbContext db,
        IClock clock,
        TimeZoneInfo zone,
        DateOnly start,
        DateOnly end,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        var completed = await db.Tasks
            .Where(t => t.CompletedAt != null
                        && t.CompletedAt >= windowStart
                        && t.CompletedAt < windowEnd)
            .Select(t => t.CompletedAt!.Value)
            .ToListAsync(ct);

        var created = await db.Tasks
            .CountAsync(t => t.CreatedAt >= windowStart && t.CreatedAt < windowEnd, ct);

        // What is overdue RIGHT NOW, not within the window: "3 gecikmiş" is a
        // statement about the present, and a task that was overdue in March and
        // has since been done is not something to report today.
        var now = clock.UtcNow;
        var openOverdue = await db.Tasks
            .CountAsync(t => t.CompletedAt == null
                             && t.Status != TaskState.Done
                             && t.DueAt != null
                             && t.DueAt < now, ct);

        var byDay = completed
            .GroupBy(instant => UserDate.LocalDayOf(instant, zone))
            .ToDictionary(g => g.Key, g => g.Count());

        // Against what was created in the same window, which is the honest
        // reading of "how much of it did I get through" — not against every open
        // task ever, where a long backlog would make a good week look like 4%.
        var pct = created == 0
            ? (completed.Count > 0 ? 100 : 0)
            : (int)Math.Round(100.0 * completed.Count / created);

        return new TaskStats(
            completed.Count,
            created,
            Math.Clamp(pct, 0, 100),
            openOverdue,
            Series(start, end, day => byDay.GetValueOrDefault(day)));
    }

    /// <summary>
    /// The 28-cell grid, oldest cell first, ending on today.
    /// </summary>
    /// <remarks>
    /// A day counts as active when the user <b>did</b> something: ran a focus
    /// session or completed a task. Opening the app is not a streak, and a grid
    /// that filled on sight would be worth nothing to look at.
    ///
    /// Always 28 days regardless of the requested window — it is a fixed
    /// component of the design, not a view over the range.
    /// </remarks>
    private static async Task<StreakStats> StreakAsync(
        AppDbContext db, TimeZoneInfo zone, DateOnly today, CancellationToken ct)
    {
        var first = today.AddDays(-(StreakDays - 1));
        var windowStart = UserDate.StartOfLocalDay(first, zone);
        var windowEnd = UserDate.EndOfLocalDay(today, zone);

        var focusDays = await db.FocusSessions
            .Where(f => f.StartedAt >= windowStart
                        && f.StartedAt < windowEnd
                        && f.FocusSeconds > 0)
            .Select(f => f.StartedAt)
            .ToListAsync(ct);

        var taskDays = await db.Tasks
            .Where(t => t.CompletedAt != null
                        && t.CompletedAt >= windowStart
                        && t.CompletedAt < windowEnd)
            .Select(t => t.CompletedAt!.Value)
            .ToListAsync(ct);

        var active = focusDays.Concat(taskDays)
            .Select(instant => UserDate.LocalDayOf(instant, zone))
            .ToHashSet();

        var days = new List<bool>(StreakDays);
        for (var offset = StreakDays - 1; offset >= 0; offset--)
        {
            days.Add(active.Contains(today.AddDays(-offset)));
        }

        var longest = 0;
        var run = 0;

        foreach (var day in days)
        {
            run = day ? run + 1 : 0;
            if (run > longest) longest = run;
        }

        // Counted backwards from today. Today being empty does NOT break the
        // streak — it is not over until the day is, and zeroing it at 00:01
        // would punish someone for not having studied yet this morning.
        var current = 0;
        for (var i = days.Count - 1; i >= 0; i--)
        {
            if (days[i]) current++;
            else if (i != days.Count - 1) break;
        }

        return new StreakStats(days, current, longest);
    }

    private static async Task<WorkoutStats> WorkoutsAsync(
        AppDbContext db,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        var sessions = await db.WorkoutSessions
            .Where(s => s.Status == WorkoutStatus.Done
                        && s.StartedAt != null
                        && s.StartedAt >= windowStart
                        && s.StartedAt < windowEnd)
            .Select(s => new { s.DurationSeconds, s.TotalSets, s.CaloriesKcal })
            .ToListAsync(ct);

        return new WorkoutStats(
            sessions.Count,
            sessions.Sum(s => s.DurationSeconds ?? 0) / 60,
            sessions.Sum(s => s.TotalSets is { } total ? (int)total : 0),
            sessions.Sum(s => s.CaloriesKcal ?? 0));
    }

    private static async Task<NutritionStats> NutritionAsync(
        AppDbContext db, DateOnly start, DateOnly end, CancellationToken ct)
    {
        // Meals are stored against a local DateOnly already, so this needs no
        // zone arithmetic — the day is the day the user filed it under.
        var mealIds = await db.Meals
            .Where(m => m.OnDate >= start && m.OnDate <= end)
            .Select(m => new { m.Id, m.OnDate })
            .ToListAsync(ct);

        var ids = mealIds.Select(m => m.Id).ToList();

        var kcalByMeal = await db.MealItems
            .Where(i => ids.Contains(i.MealId))
            .GroupBy(i => i.MealId)
            .Select(g => new { MealId = g.Key, Kcal = g.Sum(i => i.Kcal) })
            .ToListAsync(ct);

        var kcalByDay = mealIds
            .GroupJoin(kcalByMeal, m => m.Id, k => k.MealId, (m, k) => new
            {
                m.OnDate,
                Kcal = k.Sum(x => x.Kcal),
            })
            .GroupBy(x => x.OnDate)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Kcal));

        // Days with food in them, not days in the window: averaging over days
        // the user never logged would report a diet nobody was on.
        var logged = kcalByDay.Count(d => d.Value > 0);

        var measurements = await db.BodyMeasurements
            .Where(m => m.WeightKg != null)
            .OrderByDescending(m => m.OnDate)
            .Select(m => new { m.OnDate, m.WeightKg })
            .Take(2)
            .ToListAsync(ct);

        var latest = measurements.FirstOrDefault();
        var previous = measurements.Skip(1).FirstOrDefault();

        return new NutritionStats(
            logged,
            logged == 0 ? 0 : (int)Math.Round(kcalByDay.Values.Sum() / logged),
            latest?.WeightKg,
            latest is not null && previous is not null
                ? latest.WeightKg - previous.WeightKg
                : null);
    }

    private static async Task<MedicationStats> MedicationAsync(
        AppDbContext db,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken ct)
    {
        var doses = await db.MedicationDoses
            .Where(d => d.ScheduledAt >= windowStart && d.ScheduledAt < windowEnd)
            .Select(d => d.Status)
            .ToListAsync(ct);

        // Only doses that RESOLVED count. A dose due this evening is not a
        // missed one, and counting it would drag the figure down all day and
        // recover it at bedtime.
        var resolved = doses.Count(s => s != DoseStatus.Pending);
        var taken = doses.Count(s => s == DoseStatus.Taken);

        return new MedicationStats(
            resolved == 0 ? 100 : (int)Math.Round(100.0 * taken / resolved),
            taken,
            resolved);
    }

    /// <summary>
    /// One entry per day in the range, zeros included. A chart with gaps for the
    /// days nothing happened is a chart that lies about the shape of the week.
    /// </summary>
    private static List<DayValue> Series(
        DateOnly start, DateOnly end, Func<DateOnly, int> value)
    {
        var series = new List<DayValue>(end.DayNumber - start.DayNumber + 1);

        for (var day = start; day <= end; day = day.AddDays(1))
        {
            series.Add(new DayValue(day, value(day)));
        }

        return series;
    }
}
