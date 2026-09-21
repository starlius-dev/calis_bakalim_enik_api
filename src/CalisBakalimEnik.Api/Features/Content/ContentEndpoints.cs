using System.Security.Claims;
using System.Text.RegularExpressions;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Content;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Plan;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Content;

public sealed record CourseRequest(
    string Name, string? Code, string? Tint, string? Instructor, Guid? TermId);

public sealed record CourseResponse(
    Guid Id, string Name, string? Code, string Tint, string? Instructor,
    Guid? TermId, bool Archived, int OpenTasks);

public sealed record ProjectRequest(
    string Name,
    string? Description,
    string? Status,
    Guid? CourseId,
    DateOnly? StartsOn,
    DateOnly? DueOn,
    short? ProgressPct);

public sealed record ProjectResponse(
    Guid Id, string Name, string? Description, string Status, Guid? CourseId,
    DateOnly? StartsOn, DateOnly? DueOn, short ProgressPct);

public sealed record EventRequest(
    string Title,
    string? EventType,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    bool AllDay,
    string? Location,
    Guid? CourseId,
    DateTimeOffset? ReminderAt,
    bool ClearReminderAt = false);

public sealed record EventResponse(
    Guid Id, string Title, string EventType, DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt, bool AllDay, string? Location, Guid? CourseId,
    DateTimeOffset? ReminderAt);

public sealed record NoteRequest(
    string? Title, string Body, Guid? CourseId, bool Pinned);

public sealed record NoteResponse(
    Guid Id, string? Title, string Body, Guid? CourseId, bool Pinned,
    DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// Dersler, Projeler, Etkinlikler and notlar.
/// </summary>
/// <remarks>
/// Every type is an <see cref="Domain.Common.OwnedEntity"/>, so the global
/// filter has already scoped each query — and a link to a course or project
/// belonging to someone else simply does not resolve, which is why the
/// validation below doubles as an ownership check.
///
/// Cover images and attachments are deliberately absent: uploads were deferred,
/// so a course carries a tint and a two-letter code instead.
/// </remarks>
public static partial class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        var courses = app.MapGroup("/api/v1/courses").WithTags("Content").RequireAuthorization();
        courses.MapGet("/", ListCoursesAsync);
        courses.MapPost("/", CreateCourseAsync);
        courses.MapGet("/{id:guid}", GetCourseAsync);
        courses.MapPatch("/{id:guid}", UpdateCourseAsync);
        courses.MapPost("/{id:guid}/archive", ArchiveCourseAsync);
        courses.MapDelete("/{id:guid}", DeleteCourseAsync);

        var projects = app.MapGroup("/api/v1/projects").WithTags("Content").RequireAuthorization();
        projects.MapGet("/", ListProjectsAsync);
        projects.MapPost("/", CreateProjectAsync);
        projects.MapPatch("/{id:guid}", UpdateProjectAsync);
        projects.MapDelete("/{id:guid}", DeleteProjectAsync);

        var events = app.MapGroup("/api/v1/events").WithTags("Content").RequireAuthorization();
        events.MapGet("/", ListEventsAsync);
        events.MapPost("/", CreateEventAsync);
        events.MapPatch("/{id:guid}", UpdateEventAsync);
        events.MapDelete("/{id:guid}", DeleteEventAsync);

        var notes = app.MapGroup("/api/v1/notes").WithTags("Content").RequireAuthorization();
        notes.MapGet("/", ListNotesAsync);
        notes.MapPost("/", CreateNoteAsync);
        notes.MapGet("/{id:guid}", GetNoteAsync);
        notes.MapPatch("/{id:guid}", UpdateNoteAsync);
        notes.MapDelete("/{id:guid}", DeleteNoteAsync);

        return app;
    }

    // ── courses ──────────────────────────────────────────────────────────

    private static async Task<IResult> ListCoursesAsync(
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct,
        bool includeArchived = false)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var query = db.Courses.AsQueryable();
        if (!includeArchived) query = query.Where(c => c.ArchivedAt == null);

        // The open-task count is what the Dersler card shows, so it is joined
        // here rather than left to N requests from the client.
        var courses = await query
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                Course = c,
                OpenTasks = db.Tasks.Count(t => t.CourseId == c.Id && t.CompletedAt == null),
            })
            .ToListAsync(ct);

        return Results.Ok(courses.Select(c => Describe(c.Course, c.OpenTasks)));
    }

    private static async Task<IResult> GetCourseAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return Results.NotFound();

        var open = await db.Tasks.CountAsync(
            t => t.CourseId == id && t.CompletedAt == null, ct);

        return Results.Ok(Describe(course, open));
    }

    private static async Task<IResult> CreateCourseAsync(
        CourseRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (InvalidCourse(request) is { } problem) return problem;

        if (request.TermId is not null &&
            !await db.Terms.AnyAsync(t => t.Id == request.TermId, ct))
        {
            return Missing("termId", "Dönem bulunamadı.");
        }

        var course = new Course
        {
            Name = request.Name.Trim(),
            Code = request.Code?.Trim(),
            Tint = request.Tint ?? "#3B2566",
            Instructor = request.Instructor?.Trim(),
            TermId = request.TermId,
        };

        db.Courses.Add(course);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/courses/{course.Id}", Describe(course, 0));
    }

    private static async Task<IResult> UpdateCourseAsync(
        Guid id,
        CourseRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (InvalidCourse(request) is { } problem) return problem;

        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return Results.NotFound();

        course.Name = request.Name.Trim();
        course.Code = request.Code?.Trim();
        if (request.Tint is not null) course.Tint = request.Tint;
        course.Instructor = request.Instructor?.Trim();
        course.TermId = request.TermId;

        await db.SaveChangesAsync(ct);

        var open = await db.Tasks.CountAsync(
            t => t.CourseId == id && t.CompletedAt == null, ct);

        return Results.Ok(Describe(course, open));
    }

    /// <summary>
    /// Archive rather than delete: a finished term's courses still own the
    /// tasks and notes that point at them, and the statistics still count them.
    /// </summary>
    private static async Task<IResult> ArchiveCourseAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return Results.NotFound();

        course.ArchivedAt = course.ArchivedAt is null ? clock.UtcNow : null;
        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(course, 0));
    }

    private static async Task<IResult> DeleteCourseAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (course is null) return Results.NotFound();

        course.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static IResult? InvalidCourse(CourseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Missing("name", "Ders adı boş olamaz.");

        // The tint reaches the client as a colour; anything else would be
        // rendered as an error or, worse, injected into a style.
        return request.Tint is not null && !HexColour().IsMatch(request.Tint)
            ? Missing("tint", "Renk #RRGGBB biçiminde olmalı.")
            : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();

    private static CourseResponse Describe(Course c, int openTasks) => new(
        c.Id, c.Name, c.Code, c.Tint, c.Instructor, c.TermId,
        c.ArchivedAt is not null, openTasks);

    // ── projects ─────────────────────────────────────────────────────────

    private static async Task<IResult> ListProjectsAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var projects = await db.Projects
            .OrderBy(p => p.Status == ProjectStatus.Done)
            .ThenBy(p => p.DueOn == null)
            .ThenBy(p => p.DueOn)
            .ToListAsync(ct);

        return Results.Ok(projects.Select(Describe));
    }

    private static async Task<IResult> CreateProjectAsync(
        ProjectRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Name))
            return Missing("name", "Proje adı boş olamaz.");

        if (!TryParse(request.Status, out ProjectStatus status))
            return Missing("status", "NotStarted, InProgress veya Done olmalı.");

        if (request.CourseId is not null &&
            !await db.Courses.AnyAsync(c => c.Id == request.CourseId, ct))
        {
            return Missing("courseId", "Ders bulunamadı.");
        }

        var project = new Project
        {
            Name = request.Name.Trim(),
            Description = request.Description,
            Status = status,
            CourseId = request.CourseId,
            StartsOn = request.StartsOn,
            DueOn = request.DueOn,
            ProgressPct = Clamp(request.ProgressPct),
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/projects/{project.Id}", Describe(project));
    }

    private static async Task<IResult> UpdateProjectAsync(
        Guid id,
        ProjectRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return Results.NotFound();

        if (!TryParse(request.Status, out ProjectStatus status))
            return Missing("status", "NotStarted, InProgress veya Done olmalı.");

        if (!string.IsNullOrWhiteSpace(request.Name)) project.Name = request.Name.Trim();
        project.Description = request.Description;
        project.Status = status;
        project.CourseId = request.CourseId;
        project.StartsOn = request.StartsOn;
        project.DueOn = request.DueOn;
        if (request.ProgressPct is not null) project.ProgressPct = Clamp(request.ProgressPct);

        // Finishing a project fills its bar: a "Done" project sitting at 40%
        // is the kind of contradiction the user has to fix by hand otherwise.
        if (project.Status == ProjectStatus.Done && request.ProgressPct is null)
            project.ProgressPct = 100;

        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(project));
    }

    private static async Task<IResult> DeleteProjectAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (project is null) return Results.NotFound();

        project.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static short Clamp(short? value) =>
        value is null ? (short)0 : Math.Clamp(value.Value, (short)0, (short)100);

    private static ProjectResponse Describe(Project p) => new(
        p.Id, p.Name, p.Description, p.Status.ToString(), p.CourseId,
        p.StartsOn, p.DueOn, p.ProgressPct);

    // ── events ───────────────────────────────────────────────────────────

    private static async Task<IResult> ListEventsAsync(
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var query = db.Events.AsQueryable();
        if (from is not null) query = query.Where(e => e.StartsAt >= from.Value.ToUniversalTime());
        if (to is not null) query = query.Where(e => e.StartsAt <= to.Value.ToUniversalTime());

        var events = await query.OrderBy(e => e.StartsAt).Take(500).ToListAsync(ct);

        return Results.Ok(events.Select(Describe));
    }

    private static async Task<IResult> CreateEventAsync(
        EventRequest request,
        AppDbContext db,
        ReminderSync reminders,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Title))
            return Missing("title", "Başlık boş olamaz.");

        if (!TryParse(request.EventType, out EventType type))
            return Missing("eventType", "Exam, Presentation, Meeting veya SchoolEvent olmalı.");

        if (request.CourseId is not null &&
            !await db.Courses.AnyAsync(c => c.Id == request.CourseId, ct))
        {
            return Missing("courseId", "Ders bulunamadı.");
        }

        var item = new Event
        {
            Title = request.Title.Trim(),
            EventType = type,
            // Normalised to UTC: Npgsql refuses a non-zero offset against
            // timestamptz, so a phone on Istanbul time would fail outright.
            StartsAt = request.StartsAt.ToUniversalTime(),
            EndsAt = request.EndsAt?.ToUniversalTime(),
            AllDay = request.AllDay,
            Location = request.Location,
            CourseId = request.CourseId,
            ReminderAt = request.ReminderAt?.ToUniversalTime(),
        };

        db.Events.Add(item);
        await db.SaveChangesAsync(ct);

        await SyncEventReminderAsync(item, userId.Value, reminders, ct);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/events/{item.Id}", Describe(item));
    }

    private static async Task<IResult> UpdateEventAsync(
        Guid id,
        EventRequest request,
        AppDbContext db,
        ReminderSync reminders,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var item = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (item is null) return Results.NotFound();

        if (!TryParse(request.EventType, out EventType type))
            return Missing("eventType", "Exam, Presentation, Meeting veya SchoolEvent olmalı.");

        if (!string.IsNullOrWhiteSpace(request.Title)) item.Title = request.Title.Trim();
        item.EventType = type;
        item.StartsAt = request.StartsAt.ToUniversalTime();
        item.EndsAt = request.EndsAt?.ToUniversalTime();
        item.AllDay = request.AllDay;
        item.Location = request.Location;
        item.CourseId = request.CourseId;

        if (request.ClearReminderAt) item.ReminderAt = null;
        else if (request.ReminderAt is not null)
            item.ReminderAt = request.ReminderAt.Value.ToUniversalTime();

        await SyncEventReminderAsync(item, userId.Value, reminders, ct);
        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(item));
    }

    private static async Task<IResult> DeleteEventAsync(
        Guid id,
        AppDbContext db,
        ReminderSync reminders,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var item = await db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (item is null) return Results.NotFound();

        item.DeletedAt = clock.UtcNow;
        await reminders.CancelAsync("event", item.Id, ct);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task SyncEventReminderAsync(
        Event item, Guid ownerId, ReminderSync reminders, CancellationToken ct)
    {
        var zone = await reminders.ZoneOfAsync(ownerId, ct);

        await reminders.SyncAsync(
            "event",
            item.Id,
            ownerId,
            NotificationType.EventSoon,
            item.Title,
            ReminderSync.DueBody(
                item.StartsAt, item.ReminderAt ?? item.StartsAt, zone),
            $"/etkinlikler/{item.Id}",
            item.ReminderAt,
            ct);
    }

    private static EventResponse Describe(Event e) => new(
        e.Id, e.Title, e.EventType.ToString(), e.StartsAt, e.EndsAt, e.AllDay,
        e.Location, e.CourseId, e.ReminderAt);

    // ── notes ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListNotesAsync(
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct,
        Guid? courseId = null)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var query = db.Notes.Where(n => n.ArchivedAt == null);
        if (courseId is not null) query = query.Where(n => n.CourseId == courseId);

        var notes = await query
            // Pinned first, then most recently touched: the two things a note
            // list is ever sorted by.
            .OrderByDescending(n => n.Pinned)
            .ThenByDescending(n => n.UpdatedAt ?? n.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return Results.Ok(notes.Select(Describe));
    }

    private static async Task<IResult> GetNoteAsync(
        Guid id, AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
        return note is null ? Results.NotFound() : Results.Ok(Describe(note));
    }

    private static async Task<IResult> CreateNoteAsync(
        NoteRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(request.Body))
            return Missing("body", "Not boş olamaz.");

        if (request.CourseId is not null &&
            !await db.Courses.AnyAsync(c => c.Id == request.CourseId, ct))
        {
            return Missing("courseId", "Ders bulunamadı.");
        }

        var note = new Note
        {
            Title = request.Title?.Trim(),
            Body = request.Body,
            CourseId = request.CourseId,
            Pinned = request.Pinned,
        };

        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/notes/{note.Id}", Describe(note));
    }

    private static async Task<IResult> UpdateNoteAsync(
        Guid id,
        NoteRequest request,
        AppDbContext db,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return Results.NotFound();

        if (string.IsNullOrWhiteSpace(request.Body))
            return Missing("body", "Not boş olamaz.");

        note.Title = request.Title?.Trim();
        note.Body = request.Body;
        note.CourseId = request.CourseId;
        note.Pinned = request.Pinned;

        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(note));
    }

    private static async Task<IResult> DeleteNoteAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var note = await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (note is null) return Results.NotFound();

        note.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static NoteResponse Describe(Note n) => new(
        n.Id, n.Title, n.Body, n.CourseId, n.Pinned, n.CreatedAt, n.UpdatedAt);

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Absent parses to the enum's default rather than failing.</summary>
    private static bool TryParse<T>(string? value, out T parsed) where T : struct, Enum
    {
        if (value is null)
        {
            parsed = default;
            // The default of both enums here is the "first" state, which is the
            // right thing for a field the client left out.
            parsed = Enum.GetValues<T>()[0];
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed);
    }

    private static IResult Missing(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message],
        });
}
