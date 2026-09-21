using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Plan;

/// <summary>A study period — "2026 Güz".</summary>
public class Term : OwnedEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Calendar dates, not instants. A term starts on a day, not at a moment,
    /// and storing it as a timestamp makes it start three hours late for
    /// exactly one user.
    /// </summary>
    public DateOnly StartsOn { get; set; }

    public DateOnly EndsOn { get; set; }

    /// <summary>
    /// At most one per user. Enforced by a filtered unique index rather than by
    /// the handler that happens to set it.
    /// </summary>
    public bool IsCurrent { get; set; }
}

/// <summary>
/// One recurring slot in the weekly timetable — a lecture, or a "Mola".
/// </summary>
/// <remarks>
/// The course link arrives with the Content pillar. Until then a title is
/// required; once courses exist it becomes optional and a null title inherits
/// the course name, which is what docs/DATABASE.md §8.1 describes.
/// </remarks>
public class ScheduleEntry : OwnedEntity
{
    public string Title { get; set; } = string.Empty;

    /// <summary>ISO-8601: 1 Monday … 7 Sunday.</summary>
    public short DayOfWeek { get; set; }

    /// <summary>
    /// Wall clock in the user's own zone. A 09:55 lecture is at 09:55 wherever
    /// the server happens to be, and stays at 09:55 if the user travels —
    /// which is the opposite of what storing an instant would do.
    /// </summary>
    public TimeOnly StartsAt { get; set; }

    public TimeOnly EndsAt { get; set; }

    public string? Location { get; set; }

    /// <summary>
    /// RFC 5545. Not yet interpreted: every entry currently repeats weekly on
    /// <see cref="DayOfWeek"/> between <see cref="ValidFrom"/> and
    /// <see cref="ValidTo"/>, which is what a timetable is. The column exists
    /// so the data model does not need changing when it is.
    /// </summary>
    public string? RecurrenceRule { get; set; }

    public DateOnly ValidFrom { get; set; }

    /// <summary>Null means "until further notice".</summary>
    public DateOnly? ValidTo { get; set; }
}

/// <summary>
/// A study-timer run — mobile screen 07 and desktop focus mode.
/// </summary>
public class FocusSession : OwnedEntity
{
    public Guid? TaskId { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The six segments of the progress bar the design draws.</summary>
    public short PlannedBlocks { get; set; } = 6;

    public short DoneBlocks { get; set; }

    /// <summary>
    /// The MEASURED total, not the gap between start and end.
    /// </summary>
    /// <remarks>
    /// A paused timer is not wall-clock elapsed, and this number is what the
    /// Profile screen's "14 sa" and the weekly bar chart are built from.
    /// Deriving it from the timestamps would count every coffee break as study.
    /// </remarks>
    public int FocusSeconds { get; set; }
}
