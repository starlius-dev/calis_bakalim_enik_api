using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Health;

public enum MedicationFrequency : short
{
    /// <summary>Her gün.</summary>
    Daily = 1,

    /// <summary>Belirli günler — <see cref="Medication.DaysOfWeek"/> applies.</summary>
    SpecificDays = 2,

    /// <summary>Gerektiğinde. No doses are generated for these.</summary>
    AsNeeded = 3,
}

public enum DoseStatus : short
{
    Pending = 1,
    Taken = 2,
    Missed = 3,
    Skipped = 4,
}

/// <summary>
/// A medication the user takes.
/// </summary>
/// <remarks>
/// The most sensitive rows in the schema, along with body weight. Owner-scoped
/// with no sharing path, and the name NEVER appears in a notification body —
/// a reminder says "İlaç zamanı" and deep-links to the dose, because
/// notification text renders on a lock screen. See docs/DATABASE.md §8.3.
/// </remarks>
public class Medication : OwnedEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Free text — "1000 IU", "half a tablet", "5 ml". Units vary so wildly
    /// that a numeric column would force the user to lie.
    /// </summary>
    public string Dose { get; set; } = string.Empty;

    public string? Instructions { get; set; }

    public MedicationFrequency Frequency { get; set; } = MedicationFrequency.Daily;

    /// <summary>ISO days, 1 Monday … 7 Sunday. Empty unless SpecificDays.</summary>
    public short[]? DaysOfWeek { get; set; }

    public int? StockCount { get; set; }
    public string? StockUnit { get; set; }

    /// <summary>The count at which the app starts saying "running low".</summary>
    public int? LowStockAt { get; set; }

    public DateOnly StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }

    /// <summary>Backs the "Duraklatılan" filter. Paused medications generate
    /// no doses but keep their history.</summary>
    public DateTimeOffset? PausedAt { get; set; }

    public bool IsActive(DateOnly today) =>
        PausedAt is null && StartedOn <= today && (EndedOn is null || EndedOn >= today);
}

/// <summary>
/// A time of day a medication is taken. Wall clock in the user's own zone —
/// 09:00 stays 09:00 if they travel, which is what a person means.
/// </summary>
public class MedicationTime : BaseEntity
{
    public Guid MedicationId { get; set; }
    public TimeOnly TimeOfDay { get; set; }
}

/// <summary>
/// One scheduled dose.
/// </summary>
/// <remarks>
/// The one recurrence in this system that is MATERIALISED rather than expanded
/// at read time, because a dose is a fact with state — taken, missed, skipped —
/// and the 28-cell adherence grid is a query over it. The unique constraint on
/// (medication, scheduled_at) is what makes the generator idempotent.
/// See docs/DATABASE.md §8.3.
/// </remarks>
public class MedicationDose : OwnedEntity
{
    public Guid MedicationId { get; set; }
    public DateTimeOffset ScheduledAt { get; set; }
    public DoseStatus Status { get; set; } = DoseStatus.Pending;
    public DateTimeOffset? TakenAt { get; set; }
}
