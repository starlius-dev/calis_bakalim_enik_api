using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Plan;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Plan;

public sealed record CreateTaskRequest(
    string Title,
    string? Notes,
    string? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReminderAt,
    Guid? ParentTaskId);

public sealed record UpdateTaskRequest(
    string? Title,
    string? Notes,
    string? Status,
    string? Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReminderAt,
    bool ClearDueAt = false,
    bool ClearReminderAt = false);

public sealed record TaskResponse(
    Guid Id,
    string Title,
    string? Notes,
    string Status,
    string Priority,
    DateTimeOffset? DueAt,
    DateTimeOffset? ReminderAt,
    DateTimeOffset? CompletedAt,
    Guid? ParentTaskId,
    DateTimeOffset CreatedAt);

/// <summary>
/// Görevler — the first domain surface.
/// </summary>
/// <remarks>
/// Note what is NOT here: a <c>Where(OwnerId == me)</c> on every query. Tasks
/// are an <see cref="Domain.Common.OwnedEntity"/>, so the global query filter
/// has already applied it — someone else's row is invisible, and a handler that
/// forgets to filter cannot leak it. That is why a missing task and another
/// user's task both answer 404 here without any code saying so.
/// See docs/DATABASE.md §2 and docs/SECURITY.md §7.
/// </remarks>
public static class TaskEndpoints
{
    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tasks")
            .WithTags("Tasks")
            .RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/complete", CompleteAsync);
        group.MapPost("/{id:guid}/reopen", ReopenAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        string? status = null,
        string? priority = null,
        string? scope = null,
        DateTimeOffset? dueBefore = null,
        bool includeCompleted = true,
        int take = 100)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var query = db.Tasks.AsQueryable();

        if (status is not null)
        {
            if (!TryParseState(status, out var parsed)) return InvalidStatus();
            query = query.Where(t => t.Status == parsed);
        }

        if (priority is not null)
        {
            if (!TryParsePriority(priority, out var parsed)) return InvalidPriority();
            query = query.Where(t => t.Priority == parsed);
        }

        if (scope is not null)
        {
            // The four chips on the Görevler screen. Day boundaries come from
            // the USER's zone: "bugün" ending at midnight UTC would cut the
            // Turkish evening off three hours early.
            var zone = await UserZoneAsync(db, userId.Value, ct);
            var localNow = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);

            switch (scope.ToLowerInvariant())
            {
                case "today":
                    var endOfDay = EndOfLocalDay(localNow, zone);
                    // Overdue tasks belong in Today: a deadline that has passed
                    // is more urgent than one arriving this evening, not less.
                    query = query.Where(t => t.CompletedAt == null
                                             && t.DueAt != null
                                             && t.DueAt <= endOfDay);
                    break;

                case "week":
                    var endOfWeek = EndOfLocalDay(localNow.AddDays(7), zone);
                    query = query.Where(t => t.CompletedAt == null
                                             && t.DueAt != null
                                             && t.DueAt <= endOfWeek);
                    break;

                case "done":
                    query = query.Where(t => t.CompletedAt != null);
                    break;

                default:
                    return Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["scope"] = ["today, week veya done olmalı."],
                    });
            }
        }

        if (dueBefore is not null) query = query.Where(t => t.DueAt <= Utc(dueBefore));
        if (!includeCompleted) query = query.Where(t => t.CompletedAt == null);

        var tasks = await query
            // Undated tasks last: a list that puts "someday" above "due today"
            // is sorted correctly and useless.
            .OrderBy(t => t.DueAt == null)
            .ThenBy(t => t.DueAt)
            .ThenByDescending(t => t.Priority)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

        return Results.Ok(tasks.Select(Describe));
    }

    private static async Task<IResult> GetAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);

        return task is null ? Results.NotFound() : Results.Ok(Describe(task));
    }

    private static async Task<IResult> CreateAsync(
        CreateTaskRequest request,
        AppDbContext db,
        TaskReminders reminders,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Title))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Başlık boş olamaz."],
            });

        if (!TryParsePriority(request.Priority, out var priority)) return InvalidPriority();

        // A parent from another user would be invisible to this query, so this
        // doubles as the ownership check on the link.
        if (request.ParentTaskId is not null &&
            !await db.Tasks.AnyAsync(t => t.Id == request.ParentTaskId, ct))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["parentTaskId"] = ["Üst görev bulunamadı."],
            });
        }

        var task = new TaskItem
        {
            Title = request.Title.Trim(),
            Notes = request.Notes,
            Priority = priority,
            DueAt = Utc(request.DueAt),
            ReminderAt = Utc(request.ReminderAt),
            ParentTaskId = request.ParentTaskId,
        };

        // OwnerId is stamped by the interceptor on insert; it is never taken
        // from the request. See docs/DATABASE.md §2.
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct);

        await reminders.SyncAsync(task, ct);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/tasks/{task.Id}", Describe(task));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateTaskRequest request,
        AppDbContext db,
        TaskReminders reminders,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return Results.NotFound();

        if (request.Title is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Title))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["title"] = ["Başlık boş olamaz."],
                });

            task.Title = request.Title.Trim();
        }

        if (request.Notes is not null) task.Notes = request.Notes;

        if (request.Status is not null)
        {
            if (!TryParseState(request.Status, out var state)) return InvalidStatus();

            task.Status = state;

            // Status and completion are one fact, so they move together — a
            // "done" task with no completed_at breaks every streak and stat
            // that counts completions.
            task.CompletedAt = state == TaskState.Done
                ? task.CompletedAt ?? DateTimeOffset.UtcNow
                : null;
        }

        if (request.Priority is not null)
        {
            if (!TryParsePriority(request.Priority, out var priority)) return InvalidPriority();
            task.Priority = priority;
        }

        // Explicit clear flags: JSON cannot tell "absent" from "null", and a
        // PATCH that silently wiped a due date every time it omitted one would
        // be a data-loss bug disguised as an API.
        if (request.ClearDueAt) task.DueAt = null;
        else if (request.DueAt is not null) task.DueAt = Utc(request.DueAt);

        if (request.ClearReminderAt) task.ReminderAt = null;
        else if (request.ReminderAt is not null) task.ReminderAt = Utc(request.ReminderAt);

        await reminders.SyncAsync(task, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(task));
    }

    private static Task<IResult> CompleteAsync(
        Guid id,
        AppDbContext db,
        TaskReminders reminders,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct) =>
        SetCompletionAsync(id, db, reminders, principal, ct, clock.UtcNow);

    private static Task<IResult> ReopenAsync(
        Guid id,
        AppDbContext db,
        TaskReminders reminders,
        ClaimsPrincipal principal,
        CancellationToken ct) =>
        SetCompletionAsync(id, db, reminders, principal, ct, null);

    private static async Task<IResult> SetCompletionAsync(
        Guid id,
        AppDbContext db,
        TaskReminders reminders,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateTimeOffset? completedAt)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return Results.NotFound();

        task.CompletedAt = completedAt;
        task.Status = completedAt is null ? TaskState.Todo : TaskState.Done;

        // Completing drops a pending reminder; reopening earns it back, if its
        // time has not already passed.
        await reminders.SyncAsync(task, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(task));
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        AppDbContext db,
        TaskReminders reminders,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (task is null) return Results.NotFound();

        // Soft delete: the row stays for the undo the design's delete-confirm
        // screen implies, and the query filter hides it meanwhile.
        task.DeletedAt = clock.UtcNow;

        await reminders.CancelAsync(task.Id, ct);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<TimeZoneInfo> UserZoneAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var id = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TimeZone)
            .FirstOrDefaultAsync(ct);

        return QuietHours.Resolve(id);
    }

    /// <summary>Midnight at the end of that local day, as an instant.</summary>
    /// <remarks>
    /// Returned in UTC. Npgsql refuses to write or compare a DateTimeOffset
    /// whose offset is not zero against <c>timestamptz</c> — the value here
    /// would carry +03:00 and the query throws. Same instant, legal form.
    /// </remarks>
    private static DateTimeOffset EndOfLocalDay(DateTimeOffset local, TimeZoneInfo zone)
    {
        var midnight = local.Date.AddDays(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// Normalises an instant supplied by a client.
    /// </summary>
    /// <remarks>
    /// A client is entitled to send <c>2026-09-30T09:55:00+03:00</c>, and
    /// Postgres stores instants, not offsets — but Npgsql rejects a non-zero
    /// offset outright rather than converting. Without this, a phone set to
    /// Istanbul time cannot create a task with a due date at all.
    /// </remarks>
    private static DateTimeOffset? Utc(DateTimeOffset? instant) =>
        instant?.ToUniversalTime();

    private static TaskResponse Describe(TaskItem task) => new(
        task.Id,
        task.Title,
        task.Notes,
        task.Status.ToString(),
        task.Priority.ToString(),
        task.DueAt,
        task.ReminderAt,
        task.CompletedAt,
        task.ParentTaskId,
        task.CreatedAt);

    private static bool TryParseState(string value, out TaskState state) =>
        Enum.TryParse(value, ignoreCase: true, out state)
        && Enum.IsDefined(state);

    private static bool TryParsePriority(string? value, out TaskPriority priority)
    {
        if (value is null)
        {
            priority = TaskPriority.Medium;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out priority)
               && Enum.IsDefined(priority);
    }

    private static IResult InvalidStatus() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = ["Todo, Doing veya Done olmalı."],
        });

    private static IResult InvalidPriority() =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["priority"] = ["Low, Medium veya Urgent olmalı."],
        });
}
