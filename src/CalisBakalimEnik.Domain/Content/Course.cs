using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Content;

/// <summary>A subject — "Fizik".</summary>
public class Course : OwnedEntity
{
    public Guid? TermId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The two-character badge the design draws — "FZ".</summary>
    public string? Code { get; set; }

    /// <summary>
    /// Cover and badge tint, as a hex string. Stored per course because the
    /// design gives each subject its own colour and the user picks it.
    /// </summary>
    public string Tint { get; set; } = "#3B2566";

    public string? Instructor { get; set; }

    /// <summary>
    /// Archived, not deleted: a finished term's courses still own the tasks and
    /// notes that reference them, and the statistics still count them.
    /// </summary>
    public DateTimeOffset? ArchivedAt { get; set; }
}

public enum ProjectStatus : short
{
    NotStarted = 1,
    InProgress = 2,
    Done = 3,
}

public class Project : OwnedEntity
{
    public Guid? CourseId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ProjectStatus Status { get; set; } = ProjectStatus.NotStarted;

    /// <summary>
    /// Set the first time the project leaves Başlanmadı; editable (J83).
    /// </summary>
    public DateOnly? StartsOn { get; set; }
    public DateOnly? DueOn { get; set; }

    /// <summary>
    /// Set when it is marked Bitti, cleared if reopened; editable (J83).
    /// </summary>
    public DateOnly? FinishedOn { get; set; }

    /// <summary>
    /// Stored, never derived from child tasks.
    /// </summary>
    /// <remarks>
    /// The design shows a project at 100% with no tasks at all ("Kitap okuma
    /// hedefi"), so a computed percentage would contradict the screen.
    /// See docs/DATABASE.md §8.2.
    /// </remarks>
    public short ProgressPct { get; set; }
}

/// <summary>
/// One field of a project changing, for the project's history (J83: every
/// change is recorded, decided 30 Sep 2026). Values are stored as text as they
/// were shown, so the history reads the same after the fields evolve.
/// </summary>
public class ProjectChange : OwnedEntity
{
    public Guid ProjectId { get; set; }
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>created, deleted, name, description, status, progress,
    /// startsOn, finishedOn, dueOn or course.</summary>
    public string Field { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

public enum EventType : short
{
    Exam = 1,
    Presentation = 2,
    Meeting = 3,
    SchoolEvent = 4,
}

public class Event : OwnedEntity
{
    public Guid? CourseId { get; set; }

    public string Title { get; set; } = string.Empty;
    public EventType EventType { get; set; } = EventType.Exam;

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public bool AllDay { get; set; }

    public string? Location { get; set; }
    public string? RecurrenceRule { get; set; }
    public DateTimeOffset? ReminderAt { get; set; }
}

public class Note : OwnedEntity
{
    public Guid? CourseId { get; set; }

    public string? Title { get; set; }

    /// <summary>Plain text, stored and shown as typed. Not Markdown (J74).</summary>
    public string Body { get; set; } = string.Empty;

    public bool Pinned { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}
