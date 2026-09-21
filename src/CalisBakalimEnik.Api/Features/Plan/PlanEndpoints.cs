using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Notifications;
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
    int FocusSeconds);

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
        focus.MapPost("/", StartFocusAsync);
        focus.MapPatch("/{id:guid}", ProgressFocusAsync);
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

        var terms = await db.Terms.OrderByDescending(t => t.StartsOn).ToListAsync(ct);

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

    private static async Task<IResult> ListFocusAsync(
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct,
        int take = 30)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var sessions = await db.FocusSessions
            .OrderByDescending(s => s.StartedAt)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync(ct);

        return Results.Ok(sessions.Select(Describe));
    }

    private static async Task<IResult> StartFocusAsync(
        StartFocusRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

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

        return Results.Created($"/api/v1/focus-sessions/{session.Id}", Describe(session));
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

        return Results.Ok(Describe(session));
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
            session.DoneBlocks = Math.Min(blocks, session.PlannedBlocks);

        // Idempotent: ending an already-ended session keeps the first end time
        // rather than stretching it every time a retry lands.
        session.EndedAt ??= clock.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(session));
    }

    private static FocusSessionResponse Describe(FocusSession s) => new(
        s.Id, s.TaskId, s.StartedAt, s.EndedAt, s.PlannedBlocks, s.DoneBlocks, s.FocusSeconds);
}
