using System.Security.Claims;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Plan;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Plan;

public sealed record TermRequest(string Name, DateOnly StartsOn, DateOnly EndsOn, bool IsCurrent);

public sealed record TermResponse(
    Guid Id, string Name, DateOnly StartsOn, DateOnly EndsOn, bool IsCurrent);

public sealed record ScheduleEntryRequest(
    string Title,
    short DayOfWeek,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    string? Location,
    DateOnly? ValidFrom,
    DateOnly? ValidTo);

public sealed record ScheduleEntryResponse(
    Guid Id,
    string Title,
    short DayOfWeek,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    string? Location,
    DateOnly ValidFrom,
    DateOnly? ValidTo);

public sealed record StartFocusRequest(Guid? TaskId, short? PlannedBlocks);

public sealed record FocusProgressRequest(short? DoneBlocks, int? FocusSeconds);

public sealed record FocusSessionResponse(
    Guid Id,
    Guid? TaskId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    short PlannedBlocks,
    short DoneBlocks,
    int FocusSeconds,
    // J76: the timer runs on the server. The client draws it from these,
    // correcting its own clock by ServerNow.
    DateTimeOffset? PausedAt = null,
    int PausedSeconds = 0,
    int ElapsedSeconds = 0,
    DateTimeOffset? ServerNow = null);

/// <summary>The block counter on Odak (J76).</summary>
/// <param name="Since">The local day the counter counts from; see FocusRules.CounterDay.</param>
public sealed record FocusSummaryResponse(DateOnly Since, int Blocks, int FocusSeconds);

/// <summary>
/// Terms, the weekly timetable, focus sessions, and the two composite screens
/// built from them — Bugün and Hafta.
/// </summary>
/// <remarks>
/// As with tasks, nothing here filters by owner: every type is an
/// <see cref="Domain.Common.OwnedEntity"/> and the global filter has already
/// done it.
/// </remarks>
public static class PlanEndpoints
{
    public static IEndpointRouteBuilder MapPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var terms = app.MapGroup("/api/v1/terms").WithTags("Plan").RequireAuthorization();
        terms.MapGet("/", ListTermsAsync);
        terms.MapPost("/", CreateTermAsync);
        terms.MapPatch("/{id:guid}", UpdateTermAsync);
        terms.MapDelete("/{id:guid}", DeleteTermAsync);

        var schedule = app.MapGroup("/api/v1/schedule").WithTags("Plan").RequireAuthorization();
        schedule.MapGet("/", ListScheduleAsync);
        schedule.MapPost("/", CreateScheduleAsync);
        schedule.MapPatch("/{id:guid}", UpdateScheduleAsync);
        schedule.MapDelete("/{id:guid}", DeleteScheduleAsync);

        var focus = app.MapGroup("/api/v1/focus-sessions").WithTags("Plan").RequireAuthorization();
        focus.MapGet("/", ListFocusAsync);
        focus.MapGet("/active", ActiveFocusAsync);
        focus.MapGet("/summary", FocusSummaryAsync);
        focus.MapPost("/", StartFocusAsync);
        focus.MapPatch("/{id:guid}", ProgressFocusAsync);
        focus.MapPost("/{id:guid}/pause", PauseFocusAsync);
        focus.MapPost("/{id:guid}/resume", ResumeFocusAsync);
        focus.MapPost("/{id:guid}/end", EndFocusAsync);

        var views = app.MapGroup("/api/v1").WithTags("Plan").RequireAuthorization();
        views.MapGet("/agenda", PlanEndpointsViews.AgendaAsync);
        views.MapGet("/dashboard", PlanEndpointsViews.DashboardAsync);

        return app;
    }

    // ── terms ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListTermsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var terms = await db.Terms.OrderByDescending(t => t.StartsOn)
            .Take(ListLimits.Ceiling)
            .ToListAsync(ct);

        return Results.Ok(terms.Select(t =>
            new TermResponse(t.Id, t.Name, t.StartsOn, t.EndsOn, t.IsCurrent)));
    }

    private static async Task<IResult> CreateTermAsync(
        TermRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var term = new Term
        {
            Name = request.Name.Trim(),
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,
            IsCurrent = request.IsCurrent,
        };

        if (term.IsCurrent) await ClearCurrentTermAsync(db, ct);

        db.Terms.Add(term);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/terms/{term.Id}",
            new TermResponse(term.Id, term.Name, term.StartsOn, term.EndsOn, term.IsCurrent));
    }

    private static async Task<IResult> UpdateTermAsync(
        Guid id,
        TermRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (term is null) return Results.NotFound();

        // Clear the old current BEFORE flipping this one, or the unique index
        // rejects the moment both are true.
        if (request.IsCurrent && !term.IsCurrent) await ClearCurrentTermAsync(db, ct);

        term.Name = request.Name.Trim();
        term.StartsOn = request.StartsOn;
        term.EndsOn = request.EndsOn;
        term.IsCurrent = request.IsCurrent;

        await db.SaveChangesAsync(ct);

        return Results.Ok(
            new TermResponse(term.Id, term.Name, term.StartsOn, term.EndsOn, term.IsCurrent));
    }

    private static async Task<IResult> DeleteTermAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var term = await db.Terms.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (term is null) return Results.NotFound();

        term.DeletedAt = clock.UtcNow;
        term.IsCurrent = false;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static Task ClearCurrentTermAsync(AppDbContext db, CancellationToken ct) =>
        db.Terms.Where(t => t.IsCurrent)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsCurrent, false), ct);

    private static IResult? Invalid(TermRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Dönem adı boş olamaz."],
            });

        return request.EndsOn < request.StartsOn
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["endsOn"] = ["Bitiş tarihi başlangıçtan önce olamaz."],
            })
            : null;
    }

    // ── schedule ─────────────────────────────────────────────────────────

    private static async Task<IResult> ListScheduleAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var entries = await db.ScheduleEntries
            .OrderBy(e => e.DayOfWeek).ThenBy(e => e.StartsAt)
            .Take(ListLimits.Ceiling)
            .ToListAsync(ct);

        return Results.Ok(entries.Select(Describe));
    }

    private static async Task<IResult> CreateScheduleAsync(
        ScheduleEntryRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var entry = new ScheduleEntry
        {
            Title = request.Title.Trim(),
            DayOfWeek = request.DayOfWeek,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            Location = request.Location,
            ValidFrom = request.ValidFrom ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            ValidTo = request.ValidTo,
        };

        db.ScheduleEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/schedule/{entry.Id}", Describe(entry));
    }

    private static async Task<IResult> UpdateScheduleAsync(
        Guid id,
        ScheduleEntryRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var entry = await db.ScheduleEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null) return Results.NotFound();

        entry.Title = request.Title.Trim();
        entry.DayOfWeek = request.DayOfWeek;
        entry.StartsAt = request.StartsAt;
        entry.EndsAt = request.EndsAt;
        entry.Location = request.Location;
        if (request.ValidFrom is not null) entry.ValidFrom = request.ValidFrom.Value;
        entry.ValidTo = request.ValidTo;

        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(entry));
    }

    private static async Task<IResult> DeleteScheduleAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var entry = await db.ScheduleEntries.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null) return Results.NotFound();

        entry.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static IResult? Invalid(ScheduleEntryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Başlık boş olamaz."],
            });

        if (request.DayOfWeek is < 1 or > 7)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["dayOfWeek"] = ["1 (Pazartesi) ile 7 (Pazar) arasında olmalı."],
            });

        return request.EndsAt <= request.StartsAt
            ? Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["endsAt"] = ["Bitiş saati başlangıçtan sonra olmalı."],
            })
            : null;
    }

    private static ScheduleEntryResponse Describe(ScheduleEntry e) => new(
        e.Id, e.Title, e.DayOfWeek, e.StartsAt, e.EndsAt, e.Location, e.ValidFrom, e.ValidTo);

    // ── focus sessions ───────────────────────────────────────────────────

    /// <summary>
    /// Study sessions, newest first.
    /// </summary>
    /// <param name="before">
    /// The <c>startedAt</c> of the last row of the previous page. Without it
    /// this endpoint stopped at 200 rows, which a daily user passes inside a
    /// year — and everything older than that was unreachable by any request,
    /// not merely inconvenient to reach.
    ///
    /// Send back the string the API returned rather than a re-formatted one.
    /// It is a microsecond timestamp, and a client that parses it into its own
    /// date type may round it — Dart on the web truncates to milliseconds —
    /// which moves the cursor PAST rows that were never shown.
    /// </param>
    private static async Task<IResult> ListFocusAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        int take = 30,
        DateTimeOffset? before = null)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var query = db.FocusSessions.AsQueryable();

        // No tie-break on the id. Two focus sessions would have to start in the
        // same microsecond for one to be needed, and a person runs one study
        // timer at a time.
        if (before is not null) query = query.Where(s => s.StartedAt < before);

        var sessions = await query
            .OrderByDescending(s => s.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

        var now = clock.UtcNow;
        if (sessions.Any(s => FocusRules.ExpireIfOverCap(s, now)))
            await db.SaveChangesAsync(ct);

        return Results.Ok(sessions.Select(s => DescribeFocus(s, now)));
    }

    /// <summary>
    /// The one running or paused session, or 204 (J76). What the running
    /// bar and a reloaded Odak page pick the timer back up from.
    /// </summary>
    private static async Task<IResult> ActiveFocusAsync(
        AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var now = clock.UtcNow;
        var open = await OpenSessionAsync(db, now, ct);

        return open is null ? Results.NoContent() : Results.Ok(DescribeFocus(open, now));
    }

    /// <summary>
    /// The block counter (J76): blocks and focused time since the counter's
    /// day began, which is today unless a timer running since yesterday is
    /// still going.
    /// </summary>
    private static async Task<IResult> FocusSummaryAsync(
        AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var now = clock.UtcNow;
        var zone = await UserDate.ZoneAsync(db, userId.Value, ct);
        var open = await OpenSessionAsync(db, now, ct);

        var since = FocusRules.CounterDay(
            UserDate.Today(now, zone),
            open is null ? null : UserDate.LocalDayOf(open.StartedAt, zone));
        var from = UserDate.StartOfLocalDay(since, zone);

        var ended = await db.FocusSessions
            .Where(s => s.EndedAt != null && s.EndedAt >= from)
            .Select(s => new { s.DoneBlocks, s.FocusSeconds })
            .ToListAsync(ct);

        return Results.Ok(new FocusSummaryResponse(
            since, ended.Sum(s => s.DoneBlocks), ended.Sum(s => s.FocusSeconds)));
    }

    private static Task<IResult> PauseFocusAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        ChangeFocusAsync(id, db, clock, principal, FocusRules.Pause, ct);

    private static Task<IResult> ResumeFocusAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        ChangeFocusAsync(id, db, clock, principal, FocusRules.Resume, ct);

    private static async Task<IResult> ChangeFocusAsync(
        Guid id,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        Action<FocusSession, DateTimeOffset> change,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var session = await db.FocusSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        var now = clock.UtcNow;
        FocusRules.ExpireIfOverCap(session, now);
        change(session, now);
        await db.SaveChangesAsync(ct);

        return Results.Ok(DescribeFocus(session, now));
    }

    /// <summary>The caller's open session, closed first if it passed the cap.</summary>
    private static async Task<FocusSession?> OpenSessionAsync(
        AppDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.FocusSessions
            .Where(s => s.EndedAt == null)
            .OrderByDescending(s => s.StartedAt)
            .FirstOrDefaultAsync(ct);

        if (open is null) return null;
        if (!FocusRules.ExpireIfOverCap(open, now)) return open;

        await db.SaveChangesAsync(ct);
        return null;
    }

    private static async Task<IResult> StartFocusAsync(
        StartFocusRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        // One timer at a time (J76). Starting while one is open hands that one
        // back rather than stacking a second.
        var now = clock.UtcNow;
        if (await OpenSessionAsync(db, now, ct) is { } open)
            return Results.Ok(DescribeFocus(open, now));

        if (request.TaskId is not null &&
            !await db.Tasks.AnyAsync(t => t.Id == request.TaskId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["taskId"] = ["Görev bulunamadı."],
            });
        }

        var session = new FocusSession
        {
            TaskId = request.TaskId,
            StartedAt = clock.UtcNow,
            PlannedBlocks = request.PlannedBlocks is > 0 and <= 24
                ? request.PlannedBlocks.Value
                : (short)6,
        };

        db.FocusSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/focus-sessions/{session.Id}", DescribeFocus(session, now));
    }

    /// <summary>
    /// Progress from the running timer.
    /// </summary>
    /// <remarks>
    /// The client owns the count, because only it knows about pauses — the
    /// server storing "now minus started_at" would turn every interruption into
    /// study time. Values only ever move FORWARD, so a stale request arriving
    /// late cannot roll the total back.
    /// </remarks>
    private static async Task<IResult> ProgressFocusAsync(
        Guid id,
        FocusProgressRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var session = await db.FocusSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        if (request.FocusSeconds is { } seconds && seconds > session.FocusSeconds)
            session.FocusSeconds = seconds;

        if (request.DoneBlocks is { } blocks && blocks > session.DoneBlocks)
            session.DoneBlocks = Math.Min(blocks, session.PlannedBlocks);

        await db.SaveChangesAsync(ct);

        return Results.Ok(DescribeFocus(session, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> EndFocusAsync(
        Guid id,
        FocusProgressRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var session = await db.FocusSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        if (request.FocusSeconds is { } seconds && seconds > session.FocusSeconds)
            session.FocusSeconds = seconds;

        if (request.DoneBlocks is { } blocks && blocks > session.DoneBlocks)
            session.DoneBlocks = blocks;

        // The server's own count wins over a lower one from the client (J76);
        // ending an ended session changes nothing, so a retry cannot stretch it.
        var now = clock.UtcNow;
        FocusRules.ExpireIfOverCap(session, now);
        FocusRules.End(session, now);

        await db.SaveChangesAsync(ct);

        return Results.Ok(DescribeFocus(session, now));
    }

    internal static FocusSessionResponse DescribeFocus(FocusSession s, DateTimeOffset now) => new(
        s.Id, s.TaskId, s.StartedAt, s.EndedAt, s.PlannedBlocks, s.DoneBlocks,
        s.EndedAt is null ? FocusRules.ElapsedSeconds(s, now) : s.FocusSeconds,
        s.PausedAt, s.PausedSeconds, FocusRules.ElapsedSeconds(s, now), now);
}
