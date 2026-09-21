using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Health;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Health;

public sealed record MedicationRequest(
    string Name,
    string Dose,
    string? Instructions,
    string? Frequency,
    short[]? DaysOfWeek,
    IReadOnlyList<TimeOnly>? Times,
    int? StockCount,
    string? StockUnit,
    int? LowStockAt,
    DateOnly? StartedOn,
    DateOnly? EndedOn);

public sealed record MedicationResponse(
    Guid Id,
    string Name,
    string Dose,
    string? Instructions,
    string Frequency,
    short[] DaysOfWeek,
    IReadOnlyList<TimeOnly> Times,
    int? StockCount,
    string? StockUnit,
    int? LowStockAt,
    bool LowStock,
    DateOnly StartedOn,
    DateOnly? EndedOn,
    bool Paused,
    int AdherencePct);

public sealed record DoseResponse(
    Guid Id,
    Guid MedicationId,
    string MedicationName,
    string Dose,
    DateTimeOffset ScheduledAt,
    string Status,
    DateTimeOffset? TakenAt);

/// <summary>
/// İlaçlar (17, 18, 20) and the doses behind them.
/// </summary>
/// <remarks>
/// Health data: owner-scoped with no sharing path, and the medication NAME
/// never leaves through a notification — the reminder the generator queues says
/// "İlaç zamanı" and deep-links. See docs/DATABASE.md §8.3.
/// </remarks>
public static class MedicationEndpoints
{
    public static IEndpointRouteBuilder MapMedicationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/medications")
            .WithTags("Health")
            .RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPatch("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/pause", PauseAsync);
        group.MapDelete("/{id:guid}", DeleteAsync);

        var doses = app.MapGroup("/api/v1/doses")
            .WithTags("Health")
            .RequireAuthorization();

        doses.MapGet("/", ListDosesAsync);
        doses.MapPost("/{id:guid}/take", TakeAsync);
        doses.MapPost("/{id:guid}/skip", SkipAsync);

        return app;
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        bool includePaused = true)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var query = db.Medications.AsQueryable();
        if (!includePaused) query = query.Where(m => m.PausedAt == null);

        var medications = await query.OrderBy(m => m.Name).ToListAsync(ct);
        return Results.Ok(await DescribeManyAsync(db, medications, clock, ct));
    }

    private static async Task<IResult> GetAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (medication is null) return Results.NotFound();

        var described = await DescribeManyAsync(db, [medication], clock, ct);
        return Results.Ok(described[0]);
    }

    private static async Task<IResult> CreateAsync(
        MedicationRequest request,
        AppDbContext db,
        MedicationDoseService doses,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var medication = new Medication
        {
            Name = request.Name.Trim(),
            Dose = request.Dose.Trim(),
            Instructions = request.Instructions,
            Frequency = Frequency(request.Frequency),
            DaysOfWeek = request.DaysOfWeek,
            StockCount = request.StockCount,
            StockUnit = request.StockUnit,
            LowStockAt = request.LowStockAt,
            StartedOn = request.StartedOn
                        ?? DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            EndedOn = request.EndedOn,
        };

        db.Medications.Add(medication);

        foreach (var time in request.Times ?? [])
        {
            db.MedicationTimes.Add(new MedicationTime
            {
                MedicationId = medication.Id,
                TimeOfDay = time,
            });
        }

        await db.SaveChangesAsync(ct);

        // Immediately, not on the next background pass: otherwise the İlaçlar
        // screen shows nothing due for up to fifteen minutes after adding a
        // medication, which reads as the app having lost it.
        await GenerateAsync(db, doses, medication, ct);

        var described = await DescribeManyAsync(db, [medication], clock, ct);
        return Results.Created($"/api/v1/medications/{medication.Id}", described[0]);
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        MedicationRequest request,
        AppDbContext db,
        MedicationDoseService doses,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();
        if (Invalid(request) is { } problem) return problem;

        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (medication is null) return Results.NotFound();

        medication.Name = request.Name.Trim();
        medication.Dose = request.Dose.Trim();
        medication.Instructions = request.Instructions;
        medication.Frequency = Frequency(request.Frequency);
        medication.DaysOfWeek = request.DaysOfWeek;
        medication.StockCount = request.StockCount;
        medication.StockUnit = request.StockUnit;
        medication.LowStockAt = request.LowStockAt;
        if (request.StartedOn is not null) medication.StartedOn = request.StartedOn.Value;
        medication.EndedOn = request.EndedOn;

        if (request.Times is not null)
        {
            // The schedule changed, so PENDING doses ahead of now are stale.
            // Taken and missed ones are history and stay untouched.
            await db.MedicationTimes
                .Where(t => t.MedicationId == medication.Id)
                .ExecuteDeleteAsync(ct);

            await doses.ClearFutureAsync(medication.Id, ct);

            foreach (var time in request.Times)
            {
                db.MedicationTimes.Add(new MedicationTime
                {
                    MedicationId = medication.Id,
                    TimeOfDay = time,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        await GenerateAsync(db, doses, medication, ct);

        var described = await DescribeManyAsync(db, [medication], clock, ct);
        return Results.Ok(described[0]);
    }

    /// <summary>
    /// Pauses or resumes. A paused medication generates no doses and keeps
    /// every one it already has — the "Duraklatılan" filter, not a delete.
    /// </summary>
    private static async Task<IResult> PauseAsync(
        Guid id,
        AppDbContext db,
        MedicationDoseService doses,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (medication is null) return Results.NotFound();

        medication.PausedAt = medication.PausedAt is null ? clock.UtcNow : null;

        if (medication.PausedAt is not null) await doses.ClearFutureAsync(medication.Id, ct);

        await db.SaveChangesAsync(ct);

        // Resuming earns the doses back straight away.
        if (medication.PausedAt is null) await GenerateAsync(db, doses, medication, ct);

        var described = await DescribeManyAsync(db, [medication], clock, ct);
        return Results.Ok(described[0]);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var medication = await db.Medications.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (medication is null) return Results.NotFound();

        medication.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ── doses ────────────────────────────────────────────────────────────

    private static async Task<IResult> ListDosesAsync(
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int take = 200)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var start = (from ?? clock.UtcNow.AddDays(-1)).ToUniversalTime();
        var end = (to ?? clock.UtcNow.AddDays(1)).ToUniversalTime();

        var doses = await db.MedicationDoses
            .Where(d => d.ScheduledAt >= start && d.ScheduledAt <= end)
            .OrderBy(d => d.ScheduledAt)
            .Take(Math.Clamp(take, 1, 500))
            .Join(
                db.Medications,
                d => d.MedicationId,
                m => m.Id,
                (d, m) => new DoseResponse(
                    d.Id, d.MedicationId, m.Name, m.Dose,
                    d.ScheduledAt, d.Status.ToString(), d.TakenAt))
            .ToListAsync(ct);

        return Results.Ok(doses);
    }

    private static Task<IResult> TakeAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        SetDoseAsync(id, db, clock, principal, ct, DoseStatus.Taken);

    private static Task<IResult> SkipAsync(
        Guid id, AppDbContext db, IClock clock, ClaimsPrincipal principal, CancellationToken ct) =>
        SetDoseAsync(id, db, clock, principal, ct, DoseStatus.Skipped);

    private static async Task<IResult> SetDoseAsync(
        Guid id,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct,
        DoseStatus status)
    {
        if (MfaEndpoints.UserId(principal) is null) return Results.Unauthorized();

        var dose = await db.MedicationDoses.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dose is null) return Results.NotFound();

        dose.Status = status;
        dose.TakenAt = status == DoseStatus.Taken ? clock.UtcNow : null;

        // Taking a dose consumes stock. Skipping does not — the point of
        // skipping is that the tablet is still in the box.
        if (status == DoseStatus.Taken)
        {
            var medication = await db.Medications
                .FirstOrDefaultAsync(m => m.Id == dose.MedicationId, ct);

            if (medication?.StockCount is > 0) medication.StockCount--;
        }

        await db.SaveChangesAsync(ct);

        var described = await db.MedicationDoses
            .Where(d => d.Id == id)
            .Join(
                db.Medications,
                d => d.MedicationId,
                m => m.Id,
                (d, m) => new DoseResponse(
                    d.Id, d.MedicationId, m.Name, m.Dose,
                    d.ScheduledAt, d.Status.ToString(), d.TakenAt))
            .FirstAsync(ct);

        return Results.Ok(described);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task GenerateAsync(
        AppDbContext db,
        MedicationDoseService doses,
        Medication medication,
        CancellationToken ct)
    {
        var zone = await doses.ZoneOfAsync(medication.OwnerId, ct);

        if (await doses.GenerateAsync(medication, zone, ct) > 0)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adherence over the last 28 days — the streak grid on the detail screen.
    /// </summary>
    /// <remarks>
    /// Counted over doses that have RESOLVED. Pending doses in the future are
    /// not failures, and counting them would drag every new medication's
    /// adherence towards zero on the day it is added.
    /// </remarks>
    private static async Task<List<MedicationResponse>> DescribeManyAsync(
        AppDbContext db,
        List<Medication> medications,
        IClock clock,
        CancellationToken ct)
    {
        if (medications.Count == 0) return [];

        var ids = medications.Select(m => m.Id).ToList();
        var since = clock.UtcNow.AddDays(-28);

        var times = await db.MedicationTimes
            .Where(t => ids.Contains(t.MedicationId))
            .OrderBy(t => t.TimeOfDay)
            .ToListAsync(ct);

        var adherence = await db.MedicationDoses
            .Where(d => ids.Contains(d.MedicationId)
                        && d.ScheduledAt >= since
                        && d.Status != DoseStatus.Pending)
            .GroupBy(d => d.MedicationId)
            .Select(g => new
            {
                MedicationId = g.Key,
                Total = g.Count(),
                Taken = g.Count(d => d.Status == DoseStatus.Taken),
            })
            .ToListAsync(ct);

        return medications.Select(m =>
        {
            var stats = adherence.FirstOrDefault(a => a.MedicationId == m.Id);
            var pct = stats is null || stats.Total == 0
                ? 100
                : (int)Math.Round(stats.Taken * 100.0 / stats.Total);

            return new MedicationResponse(
                m.Id,
                m.Name,
                m.Dose,
                m.Instructions,
                m.Frequency.ToString(),
                m.DaysOfWeek ?? [],
                times.Where(t => t.MedicationId == m.Id)
                    .Select(t => t.TimeOfDay)
                    .ToList(),
                m.StockCount,
                m.StockUnit,
                m.LowStockAt,
                m.StockCount is not null
                && m.LowStockAt is not null
                && m.StockCount <= m.LowStockAt,
                m.StartedOn,
                m.EndedOn,
                m.PausedAt is not null,
                pct);
        }).ToList();
    }

    private static MedicationFrequency Frequency(string? value) =>
        Enum.TryParse<MedicationFrequency>(value, ignoreCase: true, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : MedicationFrequency.Daily;

    private static IResult? Invalid(MedicationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Problem("name", "İlaç adı boş olamaz.");

        if (string.IsNullOrWhiteSpace(request.Dose))
            return Problem("dose", "Doz boş olamaz.");

        if (Frequency(request.Frequency) == MedicationFrequency.SpecificDays
            && (request.DaysOfWeek is null || request.DaysOfWeek.Length == 0))
        {
            return Problem("daysOfWeek", "Belirli günler için en az bir gün seç.");
        }

        return request.DaysOfWeek?.Any(d => d is < 1 or > 7) == true
            ? Problem("daysOfWeek", "Günler 1 (Pazartesi) ile 7 (Pazar) arasında olmalı.")
            : null;
    }

    private static IResult Problem(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [field] = [message],
        });
}
