using CalisBakalimEnik.Domain.Content;

namespace CalisBakalimEnik.Api.Features.Content;

/// <summary>A project's fields as the history compares them.</summary>
public sealed record ProjectSnapshot(
    string Name,
    string? Description,
    ProjectStatus Status,
    short ProgressPct,
    DateOnly? StartsOn,
    DateOnly? FinishedOn,
    DateOnly? DueOn,
    Guid? CourseId)
{
    public static ProjectSnapshot Of(Project p) => new(
        p.Name, p.Description, p.Status, p.ProgressPct, p.StartsOn, p.FinishedOn, p.DueOn, p.CourseId);
}

/// <summary>
/// How status, progress and dates relate (J82, J83). Pure, so the rules are
/// tested without a database; the endpoints only apply them.
/// </summary>
public static class ProjectRules
{
    /// <summary>
    /// 0% is Başlanmadı, whatever was sent. Leaving 0% from Başlanmadı is
    /// Yapılıyor. Otherwise the chosen status stands, including Bitti below
    /// 100%, which is allowed on purpose.
    /// </summary>
    public static ProjectStatus Normalise(ProjectStatus status, short progressPct) =>
        progressPct <= 0 ? ProjectStatus.NotStarted
        : status == ProjectStatus.NotStarted ? ProjectStatus.InProgress
        : status;

    /// <summary>
    /// The start date fills in the first time a project is under way, the
    /// finish date when it is Bitti, and the finish date clears if it is
    /// reopened. A date the person entered is kept.
    /// </summary>
    public static (DateOnly? StartsOn, DateOnly? FinishedOn) Dates(
        ProjectStatus status, DateOnly? startsOn, DateOnly? finishedOn, DateOnly today)
    {
        if (status != ProjectStatus.NotStarted) startsOn ??= today;
        finishedOn = status == ProjectStatus.Done ? finishedOn ?? today : null;
        return (startsOn, finishedOn);
    }

    /// <summary>Every field that differs, as the history stores it.</summary>
    public static IReadOnlyList<(string Field, string? Old, string? New)> Diff(
        ProjectSnapshot before, ProjectSnapshot after)
    {
        var changes = new List<(string, string?, string?)>();

        void Add<T>(string field, T old, T @new, Func<T, string?> show)
        {
            if (!EqualityComparer<T>.Default.Equals(old, @new))
                changes.Add((field, show(old), show(@new)));
        }

        Add("name", before.Name, after.Name, v => v);
        Add("description", before.Description, after.Description, v => v);
        Add("status", before.Status, after.Status, v => v.ToString());
        Add("progress", before.ProgressPct, after.ProgressPct, v => v.ToString());
        Add("startsOn", before.StartsOn, after.StartsOn, Day);
        Add("finishedOn", before.FinishedOn, after.FinishedOn, Day);
        Add("dueOn", before.DueOn, after.DueOn, Day);
        Add("course", before.CourseId, after.CourseId, v => v?.ToString());

        return changes;
    }

    private static string? Day(DateOnly? d) => d?.ToString("yyyy-MM-dd");
}
