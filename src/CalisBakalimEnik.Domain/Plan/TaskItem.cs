using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Plan;

/// <summary>
/// Not <c>TaskStatus</c>: that name is taken by
/// <see cref="System.Threading.Tasks.TaskStatus"/>, which implicit usings bring
/// into every file in this project.
/// </summary>
public enum TaskState : short
{
    Todo = 1,
    Doing = 2,
    Done = 3,
}

/// <summary>
/// Düşük / Orta / Acil — exactly the three the design draws, each with its own
/// pill colour pair.
/// </summary>
/// <remarks>
/// Not a 1–5 scale. The UI has no way to render two of those values, so storing
/// them would produce rows the app cannot display. See docs/DATABASE.md §8.1.
/// </remarks>
public enum TaskPriority : short
{
    Low = 1,
    Medium = 2,
    Urgent = 3,
}

/// <summary>
/// A task. <c>TaskItem</c> rather than <c>Task</c> for the same reason as
/// <see cref="TaskState"/>: the bare name is the BCL's.
/// </summary>
/// <remarks>
/// The first <see cref="OwnedEntity"/> in the system, so it is the first row
/// the global ownership filter actually protects: someone else's task is
/// invisible, not merely un-returned by a handler that remembered to filter.
///
/// Course and project links arrive with the Content pillar — the columns are
/// deliberately absent until the tables they would reference exist, rather than
/// present and pointing at nothing.
/// </remarks>
public class TaskItem : OwnedEntity
{
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }

    public TaskState Status { get; set; } = TaskState.Todo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;

    public DateTimeOffset? DueAt { get; set; }

    /// <summary>
    /// An absolute instant, computed from the user's local intent at write
    /// time. The scheduler then never does time-zone arithmetic — it only asks
    /// whether this moment has passed. See docs/NOTIFICATIONS.md §6.
    /// </summary>
    public DateTimeOffset? ReminderAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Subtasks. Deleting a parent takes its children with it.</summary>
    public Guid? ParentTaskId { get; set; }
}
