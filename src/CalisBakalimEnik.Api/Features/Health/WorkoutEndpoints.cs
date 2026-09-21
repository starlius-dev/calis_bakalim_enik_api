using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Health;

public sealed record ExerciseRequest(string Name, string? Category, string[]? Muscles);

public sealed record ExerciseResponse(
    Guid Id, string Name, string Category, string[] Muscles, bool IsSystem);

public sealed record PlanItemRequest(
    Guid ExerciseId, short TargetSets, short? TargetReps, short? TargetSeconds);

public sealed record WorkoutPlanRequest(
    string Name, short[] DaysOfWeek, IReadOnlyList<PlanItemRequest>? Items);

public sealed record PlanItemResponse(
    Guid Id, Guid ExerciseId, string ExerciseName, short Position,
    short TargetSets, short? TargetReps, short? TargetSeconds);

public sealed record WorkoutPlanResponse(
    Guid Id, string Name, short[] DaysOfWeek, IReadOnlyList<PlanItemResponse> Items);

public sealed record StartWorkoutRequest(Guid? PlanId, DateOnly? ScheduledOn);

public sealed record LogSetRequest(
    Guid ExerciseId, short SetNumber, short? Reps, short? Seconds, decimal? WeightKg);

public sealed record FinishWorkoutRequest(int? CaloriesKcal);

public sealed record WorkoutSetResponse(
    Guid Id, Guid ExerciseId, string ExerciseName, short SetNumber,
    short? Reps, short? Seconds, decimal? WeightKg, DateTimeOffset? CompletedAt);

public sealed record WorkoutSessionResponse(
    Guid Id,
    Guid? PlanId,
    string Status,
    DateOnly? ScheduledOn,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    int? DurationSeconds,
    int? CaloriesKcal,
    short? TotalSets,
    IReadOnlyList<WorkoutSetResponse> Sets);

/// <summary>
/// Antrenman — the catalogue, the plans, and the live session Spor modu writes.
/// </summary>
public static class WorkoutEndpoints
{
    public static IEndpointRouteBuilder MapWorkoutEndpoints(this IEndpointRouteBuilder app)
    {
        var exercises = app.MapGroup("/api/v1/exercises")
            .WithTags("Health").RequireAuthorization();
        exercises.MapGet("/", ListExercisesAsync);
        exercises.MapPost("/", CreateExerciseAsync);
        exercises.MapDelete("/{id:guid}", DeleteExerciseAsync);

        var plans = app.MapGroup("/api/v1/workout-plans")
            .WithTags("Health").RequireAuthorization();
        plans.MapGet("/", ListPlansAsync);
        plans.MapPost("/", CreatePlanAsync);
        plans.MapDelete("/{id:guid}", DeletePlanAsync);

        var sessions = app.MapGroup("/api/v1/workout-sessions")
            .WithTags("Health").RequireAuthorization();
        sessions.MapGet("/", ListSessionsAsync);
        sessions.MapPost("/", StartSessionAsync);
        sessions.MapPost("/{id:guid}/sets", LogSetAsync);
        sessions.MapPost("/{id:guid}/finish", FinishSessionAsync);
        sessions.MapDelete("/{id:guid}", DeleteSessionAsync);

        return app;
    }

    // ── exercises ────────────────────────────────────────────────────────

    /// <summary>
    /// The catalogue: the user's own plus the system ones.
    /// </summary>
    /// <remarks>
    /// Exercises are NOT an OwnedEntity — a system row has no owner and the
    /// global filter would hide it — so the scoping is explicit here. Getting
    /// this wrong in either direction is a bug: too narrow hides the catalogue,
    /// too wide leaks other users' custom exercises.
    /// </remarks>
    private static async Task<IResult> ListExercisesAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct, string? q = null)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var query = db.Exercises
            .Where(e => e.OwnerId == userId || e.OwnerId == null);

        if (!string.IsNullOrWhiteSpace(q))
            query = query.Where(e => EF.Functions.ILike(e.Name, $"%{q}%"));

        var exercises = await query
            .OrderBy(e => e.Name)
            .Take(200)
            .Select(e => new ExerciseResponse(
                e.Id, e.Name, e.Category.ToString(), e.Muscles, e.IsSystem))
            .ToListAsync(ct);

        return Results.Ok(exercises);
    }

    private static async Task<IResult> CreateExerciseAsync(
        ExerciseRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("name", "Hareket adı boş olamaz.");

        var exercise = new Exercise
        {
            OwnerId = userId,
            Name = request.Name.Trim(),
            Muscles = request.Muscles ?? [],
            Category = Enum.TryParse<ExerciseCategory>(
                request.Category, ignoreCase: true, out var category)
                ? category
                : ExerciseCategory.Strength,
            IsSystem = false,
        };

        db.Exercises.Add(exercise);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/v1/exercises/{exercise.Id}",
            new ExerciseResponse(
                exercise.Id, exercise.Name, exercise.Category.ToString(),
                exercise.Muscles, false));
    }

    private static async Task<IResult> DeleteExerciseAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        // Only your own: a system exercise is not yours to remove.
        var exercise = await db.Exercises
            .FirstOrDefaultAsync(e => e.Id == id && e.OwnerId == userId, ct);

        if (exercise is null) return Results.NotFound();

        // Still referenced by a plan or a logged set? Refuse rather than
        // orphan history — the FK is RESTRICT and would throw anyway.
        var used = await db.WorkoutSets.AnyAsync(s => s.ExerciseId == id, ct)
                   || await db.WorkoutPlanItems.AnyAsync(i => i.ExerciseId == id, ct);

        if (used)
        {
            return Results.Conflict(new
            {
                title = "Bu hareket bir planda ya da geçmiş antrenmanda kullanılıyor.",
            });
        }

        db.Exercises.Remove(exercise);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── plans ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListPlansAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var plans = await db.WorkoutPlans.OrderBy(p => p.Name).ToListAsync(ct);
        var ids = plans.Select(p => p.Id).ToList();

        var items = await db.WorkoutPlanItems
            .Where(i => ids.Contains(i.PlanId))
            .OrderBy(i => i.Position)
            .Join(
                db.Exercises,
                i => i.ExerciseId,
                e => e.Id,
                (i, e) => new { i.PlanId, Item = new PlanItemResponse(
                    i.Id, i.ExerciseId, e.Name, i.Position,
                    i.TargetSets, i.TargetReps, i.TargetSeconds) })
            .ToListAsync(ct);

        return Results.Ok(plans.Select(p => new WorkoutPlanResponse(
            p.Id,
            p.Name,
            p.DaysOfWeek,
            items.Where(i => i.PlanId == p.Id).Select(i => i.Item).ToList())));
    }

    private static async Task<IResult> CreatePlanAsync(
        WorkoutPlanRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return Invalid("name", "Plan adı boş olamaz.");

        if (request.DaysOfWeek.Any(d => d is < 1 or > 7))
            return Invalid("daysOfWeek", "Günler 1 ile 7 arasında olmalı.");

        var plan = new WorkoutPlan
        {
            Name = request.Name.Trim(),
            DaysOfWeek = request.DaysOfWeek,
        };

        db.WorkoutPlans.Add(plan);

        short position = 0;

        foreach (var item in request.Items ?? [])
        {
            // The exercise must be visible to this user: their own or a system
            // one. Anything else is someone else's row.
            var exists = await db.Exercises.AnyAsync(
                e => e.Id == item.ExerciseId
                     && (e.OwnerId == userId || e.OwnerId == null), ct);

            if (!exists) return Invalid("items", "Hareket bulunamadı.");

            if (item.TargetReps is null && item.TargetSeconds is null)
                return Invalid("items", "Tekrar ya da süre hedefi gerekli.");

            db.WorkoutPlanItems.Add(new WorkoutPlanItem
            {
                PlanId = plan.Id,
                ExerciseId = item.ExerciseId,
                Position = position++,
                TargetSets = item.TargetSets < 1 ? (short)1 : item.TargetSets,
                TargetReps = item.TargetReps,
                TargetSeconds = item.TargetSeconds,
            });
        }

        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/workout-plans/{plan.Id}", new WorkoutPlanResponse(
            plan.Id, plan.Name, plan.DaysOfWeek, []));
    }

    private static async Task<IResult> DeletePlanAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var plan = await db.WorkoutPlans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan is null) return Results.NotFound();

        plan.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── sessions ─────────────────────────────────────────────────────────

    private static async Task<IResult> ListSessionsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct, int take = 30)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var sessions = await db.WorkoutSessions
            .OrderByDescending(s => s.StartedAt ?? DateTimeOffset.MinValue)
            .Take(Math.Clamp(take, 1, 100))
            .ToListAsync(ct);

        return Results.Ok(await DescribeAsync(db, sessions, ct));
    }

    private static async Task<IResult> StartSessionAsync(
        StartWorkoutRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        if (request.PlanId is not null &&
            !await db.WorkoutPlans.AnyAsync(p => p.Id == request.PlanId, ct))
        {
            return Invalid("planId", "Plan bulunamadı.");
        }

        var session = new WorkoutSession
        {
            PlanId = request.PlanId,
            Status = WorkoutStatus.Active,
            ScheduledOn = request.ScheduledOn
                          ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            StartedAt = clock.UtcNow,
        };

        db.WorkoutSessions.Add(session);
        await db.SaveChangesAsync(ct);

        var described = await DescribeAsync(db, [session], ct);
        return Results.Created(
            $"/api/v1/workout-sessions/{session.Id}", described[0]);
    }

    /// <summary>
    /// One set. The hot path during Spor modu — called once per set.
    /// </summary>
    private static async Task<IResult> LogSetAsync(
        Guid id,
        LogSetRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var session = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        var exercise = await db.Exercises.AnyAsync(
            e => e.Id == request.ExerciseId
                 && (e.OwnerId == userId || e.OwnerId == null), ct);

        if (!exercise) return Invalid("exerciseId", "Hareket bulunamadı.");

        if (request.Reps is null && request.Seconds is null)
            return Invalid("reps", "Tekrar ya da süre gerekli.");

        var set = new WorkoutSet
        {
            SessionId = session.Id,
            ExerciseId = request.ExerciseId,
            SetNumber = request.SetNumber,
            Reps = request.Reps,
            Seconds = request.Seconds,
            WeightKg = request.WeightKg,
            CompletedAt = clock.UtcNow,
        };

        db.WorkoutSets.Add(set);
        await db.SaveChangesAsync(ct);

        var name = await db.Exercises
            .Where(e => e.Id == set.ExerciseId)
            .Select(e => e.Name)
            .FirstAsync(ct);

        return Results.Created(
            $"/api/v1/workout-sessions/{session.Id}/sets/{set.Id}",
            new WorkoutSetResponse(
                set.Id, set.ExerciseId, name, set.SetNumber,
                set.Reps, set.Seconds, set.WeightKg, set.CompletedAt));
    }

    /// <summary>
    /// Ends the session and DENORMALISES its totals.
    /// </summary>
    /// <remarks>
    /// The summary (41 dk · 318 kcal · 17 set) and the statistics screen open
    /// constantly and the sets never change once done, so the aggregate is
    /// computed once here rather than on every read.
    /// </remarks>
    private static async Task<IResult> FinishSessionAsync(
        Guid id,
        FinishWorkoutRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var session = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        var sets = await db.WorkoutSets.CountAsync(s => s.SessionId == id, ct);

        // Idempotent, like the focus timer: finishing twice keeps the first
        // end time rather than stretching the session on every retry.
        session.EndedAt ??= clock.UtcNow;
        session.Status = WorkoutStatus.Done;
        session.TotalSets = (short)sets;
        session.DurationSeconds = session.StartedAt is null
            ? null
            : (int)(session.EndedAt.Value - session.StartedAt.Value).TotalSeconds;

        if (request.CaloriesKcal is not null) session.CaloriesKcal = request.CaloriesKcal;

        await db.SaveChangesAsync(ct);

        var described = await DescribeAsync(db, [session], ct);
        return Results.Ok(described[0]);
    }

    private static async Task<IResult> DeleteSessionAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var session = await db.WorkoutSessions.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return Results.NotFound();

        session.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<List<WorkoutSessionResponse>> DescribeAsync(
        AppDbContext db, List<WorkoutSession> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0) return [];

        var ids = sessions.Select(s => s.Id).ToList();

        var sets = await db.WorkoutSets
            .Where(s => ids.Contains(s.SessionId))
            .OrderBy(s => s.SetNumber)
            .Join(
                db.Exercises,
                s => s.ExerciseId,
                e => e.Id,
                (s, e) => new { s.SessionId, Set = new WorkoutSetResponse(
                    s.Id, s.ExerciseId, e.Name, s.SetNumber,
                    s.Reps, s.Seconds, s.WeightKg, s.CompletedAt) })
            .ToListAsync(ct);

        return sessions.Select(s => new WorkoutSessionResponse(
            s.Id, s.PlanId, s.Status.ToString(), s.ScheduledOn, s.StartedAt,
            s.EndedAt, s.DurationSeconds, s.CaloriesKcal, s.TotalSets,
            sets.Where(x => x.SessionId == s.Id).Select(x => x.Set).ToList())).ToList();
    }

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message],
        });
}
