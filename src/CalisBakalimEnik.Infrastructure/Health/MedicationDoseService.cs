using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Health;

/// <summary>
/// Materialises medication doses and marks the ones that quietly passed.
/// </summary>
/// <remarks>
/// Called from TWO places, and it matters that both exist:
///
/// - the endpoints, the moment a medication is added, edited or resumed, so
///   the İlaçlar screen shows today's doses immediately. Leaving this to the
///   background pass meant a user could add "09:00 D Vitamini" and see nothing
///   due for up to fifteen minutes, which reads as the app having lost it.
/// - <see cref="MedicationDoseGenerator"/>, which extends the horizon and
///   sweeps missed doses.
///
/// Re-running is always safe: every insert relies on the unique constraint on
/// (medication, scheduled_at). See docs/DATABASE.md §8.3.
/// </remarks>
public sealed class MedicationDoseService(
    AppDbContext db,
    NotificationService notifications,
    IClock clock)
{
    /// <summary>How far ahead doses exist.</summary>
    public const int HorizonDays = 14;

    /// <summary>
    /// How late a dose may be taken before it counts as missed. A reminder at
    /// 09:00 ticked at 11:00 was still taken.
    /// </summary>
    public static readonly TimeSpan MissedGrace = TimeSpan.FromHours(4);

    /// <summary>
    /// A second nudge for a dose still unmarked this long after its time
    /// (J65, decided 30 Sep 2026). Dropped, like the first, once the dose is
    /// taken or skipped.
    /// </summary>
    public static readonly TimeSpan FollowUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>The entity type a dose's reminders carry; EntityId is the dose.</summary>
    public const string DoseEntity = "medication_dose";

    /// <summary>
    /// Fills the horizon for one medication. Does NOT save — the caller owns
    /// the transaction, so the doses and whatever prompted them land together.
    /// </summary>
    public async Task<int> GenerateAsync(
        Medication medication, TimeZoneInfo zone, CancellationToken ct)
    {
        if (medication.DeletedAt is not null) return 0;
        if (medication.PausedAt is not null) return 0;
        if (medication.Frequency == MedicationFrequency.AsNeeded) return 0;

        var times = await db.MedicationTimes
            .Where(t => t.MedicationId == medication.Id)
            .ToListAsync(ct);

        if (times.Count == 0) return 0;

        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

        // The "already generated" window has to cover exactly what the loop
        // below can produce, and it is expressed in the user's LOCAL DAYS for
        // the same reason the loop is.
        //
        // It used to be `now .. now.AddDays(HorizonDays)` — a flat 14x24h from
        // the current instant — while the loop runs to the END of the 14th
        // local day. Everything in that gap was generated but invisible to the
        // dedupe check, so the next pass inserted it again and PostgreSQL
        // refused on uq_dose(medication_id, scheduled_at).
        //
        // That is not a stray warning. Every medication's doses go into one
        // SaveChanges, so a single duplicate aborted the whole sweep: no doses
        // created for anybody, and MarkMissedAsync never reached. A live
        // restart found it at 21:00 Istanbul on day fourteen — three hours past
        // the old window.
        var windowStart = ToInstant(today.AddDays(-1), TimeOnly.MinValue, zone);
        var windowEnd = ToInstant(today.AddDays(HorizonDays + 1), TimeOnly.MinValue, zone);

        var existing = await db.MedicationDoses
            .IgnoreQueryFilters()
            .Where(d => d.MedicationId == medication.Id
                        && d.ScheduledAt >= windowStart
                        && d.ScheduledAt < windowEnd)
            .Select(d => d.ScheduledAt)
            .ToListAsync(ct);

        var seen = existing.ToHashSet();
        var created = 0;

        for (var offset = 0; offset <= HorizonDays; offset++)
        {
            var day = today.AddDays(offset);

            if (day < medication.StartedOn) continue;
            if (medication.EndedOn is not null && day > medication.EndedOn) continue;
            if (!AppliesOn(medication, day)) continue;

            foreach (var time in times)
            {
                var instant = ToInstant(day, time.TimeOfDay, zone);

                // Nothing in the past: a dose that never existed was never
                // missed either.
                if (instant < now) continue;
                if (!seen.Add(instant)) continue;

                var dose = new MedicationDose
                {
                    OwnerId = medication.OwnerId,
                    MedicationId = medication.Id,
                    ScheduledAt = instant,
                    Status = DoseStatus.Pending,
                };
                db.MedicationDoses.Add(dose);

                // Both carry the DOSE id (they used to carry the medication's,
                // so nothing could tell which dose a reminder was for, or drop
                // it once that dose was handled). NOT the medication name in
                // the text: this renders on a lock screen. docs/DATABASE.md §8.3.
                notifications.Queue(
                    medication.OwnerId,
                    NotificationType.MedicationDue,
                    "İlaç zamanı",
                    "Dozunu almayı unutma.",
                    route: $"/ilaclar/{medication.Id}",
                    entityType: DoseEntity,
                    entityId: dose.Id,
                    scheduledAt: instant);

                notifications.Queue(
                    medication.OwnerId,
                    NotificationType.MedicationDue,
                    "Dozunu işaretlemedin",
                    "Aldıysan ya da atladıysan uygulamada işaretle.",
                    route: $"/ilaclar/{medication.Id}",
                    entityType: DoseEntity,
                    entityId: dose.Id,
                    scheduledAt: instant + FollowUpAfter);

                created++;
            }
        }

        return created;
    }

    /// <summary>
    /// Drops pending doses ahead of now — for a schedule change or a pause,
    /// where the doses on the books no longer reflect what the user takes.
    /// History (taken, missed, skipped) is never touched.
    /// </summary>
    /// <remarks>
    /// Their reminders go with them. They used to stay behind, so pausing a
    /// medication still sent two weeks of "İlaç zamanı" for doses that no
    /// longer existed.
    /// </remarks>
    public async Task<int> ClearFutureAsync(Guid medicationId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var doses = await db.MedicationDoses
            .Where(d => d.MedicationId == medicationId
                        && d.Status == DoseStatus.Pending
                        && d.ScheduledAt > now)
            .Select(d => d.Id)
            .ToListAsync(ct);

        await db.Notifications
            .Where(n => n.EntityType == DoseEntity
                        && n.QueuedAt == null
                        && n.SentAt == null
                        // Reminders written before 30 Sep 2026 carry the
                        // medication's id instead of the dose's.
                        && (doses.Contains(n.EntityId!.Value)
                            || (n.EntityId == medicationId && n.ScheduledAt > now)))
            .ExecuteDeleteAsync(ct);

        return await db.MedicationDoses
            .Where(d => doses.Contains(d.Id))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Drops a dose's reminders that have not gone out yet, once it is taken
    /// or skipped (J65): a reminder for a dose already handled is noise.
    /// </summary>
    public Task<int> DropRemindersAsync(Guid doseId, CancellationToken ct) =>
        db.Notifications
            .Where(n => n.EntityType == DoseEntity
                        && n.EntityId == doseId
                        && n.QueuedAt == null
                        && n.SentAt == null)
            .ExecuteDeleteAsync(ct);

    /// <summary>Anything still pending well past its time was not taken.</summary>
    public Task<int> MarkMissedAsync(CancellationToken ct)
    {
        var cutoff = clock.UtcNow - MissedGrace;

        return db.MedicationDoses
            .IgnoreQueryFilters()
            .Where(d => d.Status == DoseStatus.Pending && d.ScheduledAt < cutoff)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.Status, DoseStatus.Missed), ct);
    }

    public async Task<TimeZoneInfo> ZoneOfAsync(Guid ownerId, CancellationToken ct)
    {
        var id = await db.Users
            .Where(u => u.Id == ownerId)
            .Select(u => u.TimeZone)
            .FirstOrDefaultAsync(ct);

        return QuietHours.Resolve(id);
    }

    private static bool AppliesOn(Medication medication, DateOnly day)
    {
        if (medication.Frequency == MedicationFrequency.Daily) return true;
        if (medication.DaysOfWeek is not { Length: > 0 }) return false;

        var iso = (short)(day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek);
        return medication.DaysOfWeek.Contains(iso);
    }

    /// <summary>
    /// A wall-clock time on a local day, as an instant. The offset is read AT
    /// that local time: a dose two weeks out can sit on the far side of a DST
    /// change, and reusing today's offset would schedule it an hour wrong.
    /// </summary>
    private static DateTimeOffset ToInstant(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
