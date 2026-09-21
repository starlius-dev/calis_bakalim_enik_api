using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Health;

public enum ExerciseCategory : short
{
    Strength = 1,
    Cardio = 2,
    Mobility = 3,
}

public enum WorkoutStatus : short
{
    Planned = 1,
    Active = 2,
    Done = 3,
    Rest = 4,
}

/// <summary>
/// The exercise catalogue: system entries everyone sees, plus a user's own.
/// </summary>
/// <remarks>
/// Not an <see cref="OwnedEntity"/>, because a system exercise has no owner at
/// all and the ownership filter would hide every one of them. Scoping is
/// explicit: a query returns rows where <see cref="OwnerId"/> is the caller's
/// OR null.
/// </remarks>
public class Exercise : AuditableEntity
{
    /// <summary>Null for a system exercise.</summary>
    public Guid? OwnerId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"göğüs", "triceps" — free tags, shown as chips.</summary>
    public string[] Muscles { get; set; } = [];

    public ExerciseCategory Category { get; set; } = ExerciseCategory.Strength;

    public bool IsSystem { get; set; }
}

public class WorkoutPlan : OwnedEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The day chips the design draws. ISO: 1 Monday … 7 Sunday.</summary>
    public short[] DaysOfWeek { get; set; } = [];
}

public class WorkoutPlanItem : BaseEntity
{
    public Guid PlanId { get; set; }
    public Guid ExerciseId { get; set; }

    public short Position { get; set; }
    public short TargetSets { get; set; } = 3;

    /// <summary>Null for a time-based exercise.</summary>
    public short? TargetReps { get; set; }

    /// <summary>"3 × 45 sn" plank. Null for a rep-based exercise.</summary>
    public short? TargetSeconds { get; set; }
}

/// <summary>
/// One workout — planned, in progress, or finished.
/// </summary>
/// <remarks>
/// The totals are DENORMALISED when the session completes rather than
/// aggregated on every read: the summary and statistics screens open
/// constantly, and the set rows behind them never change once done.
/// See docs/DATABASE.md §8.3.
/// </remarks>
public class WorkoutSession : OwnedEntity
{
    public Guid? PlanId { get; set; }
    public WorkoutStatus Status { get; set; } = WorkoutStatus.Planned;

    public DateOnly? ScheduledOn { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public int? DurationSeconds { get; set; }
    public int? CaloriesKcal { get; set; }
    public short? TotalSets { get; set; }
}

/// <summary>
/// One set, written live by Spor modu.
/// </summary>
/// <remarks>
/// This is the hot path during a workout — one POST per set — so it stays a
/// thin row with no aggregation of its own.
/// </remarks>
public class WorkoutSet : BaseEntity
{
    public Guid SessionId { get; set; }
    public Guid ExerciseId { get; set; }

    public short SetNumber { get; set; }
    public short? Reps { get; set; }
    public short? Seconds { get; set; }
    public decimal? WeightKg { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}
