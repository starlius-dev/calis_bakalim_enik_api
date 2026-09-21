using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Health;

public enum MealType : short
{
    Breakfast = 1,
    Snack = 2,
    Lunch = 3,
    Dinner = 4,
}

/// <summary>
/// The food catalogue.
/// </summary>
/// <remarks>
/// User-entered by decision: no external catalogue is imported, so every row
/// here was typed by someone. The <see cref="OwnerId"/>-null case is kept for a
/// seeded catalogue arriving later without a schema change.
///
/// Like <see cref="Exercise"/>, not an OwnedEntity: a system food has no owner
/// and the filter would hide it.
/// </remarks>
public class Food : AuditableEntity
{
    public Guid? OwnerId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"100 g", "1 porsiyon" — what one unit of this food is.</summary>
    public string ServingDesc { get; set; } = string.Empty;

    public decimal? ServingGrams { get; set; }

    public decimal Kcal { get; set; }
    public decimal? ProteinG { get; set; }
    public decimal? CarbG { get; set; }
    public decimal? FatG { get; set; }

    public bool IsSystem { get; set; }
}

/// <summary>One meal on one day. At most one of each type per day.</summary>
public class Meal : OwnedEntity
{
    public DateOnly OnDate { get; set; }
    public MealType MealType { get; set; }
}

/// <summary>
/// One food in one meal.
/// </summary>
/// <remarks>
/// The macros are SNAPSHOTTED here at the moment it is logged. If a food's
/// catalogue values are corrected later, yesterday's total must not silently
/// change — a nutrition log that rewrites history is worse than useless.
/// <see cref="FoodId"/> is kept for traceability only.
/// See docs/DATABASE.md §8.3.
/// </remarks>
public class MealItem : BaseEntity
{
    public Guid MealId { get; set; }
    public Guid FoodId { get; set; }

    public decimal Quantity { get; set; } = 1;

    public decimal Kcal { get; set; }
    public decimal? ProteinG { get; set; }
    public decimal? CarbG { get; set; }
    public decimal? FatG { get; set; }
}

/// <summary>Daily targets. One row per user, keyed by owner.</summary>
public class HealthGoal
{
    public Guid OwnerId { get; set; }

    public int? KcalTarget { get; set; }
    public short? ProteinTargetG { get; set; }
    public short? CarbTargetG { get; set; }
    public short? FatTargetG { get; set; }
    public short? WorkoutsPerWeek { get; set; }
    public decimal? WeightTargetKg { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Body weight on a date. One per day — logging twice corrects the entry
/// rather than creating a second truth.
/// </summary>
/// <remarks>Health data: owner-scoped, never in a notification body.</remarks>
public class BodyMeasurement : OwnedEntity
{
    public DateOnly OnDate { get; set; }
    public decimal? WeightKg { get; set; }
}
