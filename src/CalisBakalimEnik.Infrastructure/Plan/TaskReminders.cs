using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Plan;

/// <summary>
/// Keeps a task's reminder and its notification in step.
/// </summary>
/// <remarks>
/// Called on every write to a task, and always as "cancel, then re-queue if it
/// still applies". Trying to patch an existing notification in place means
/// reasoning about which of the moved, cleared, completed and deleted cases got
/// there — this way there is one path and it is idempotent.
///
/// Both halves run inside the CALLER's transaction: the task change and its
/// reminder either both land or neither does.
/// </remarks>
public sealed class TaskReminders(AppDbContext db, NotificationService notifications)
{
    internal const string EntityType = "task";

    public async Task SyncAsync(TaskItem task, CancellationToken ct)
    {
        await CancelAsync(task.Id, ct);

        // Nothing to remind about: no time set, or the task is finished or gone.
        if (task.ReminderAt is null) return;
        if (task.CompletedAt is not null) return;
        if (task.DeletedAt is not null) return;

        var user = await db.Users
            .Where(u => u.Id == task.OwnerId)
            .Select(u => new { u.TimeZone })
            .FirstOrDefaultAsync(ct);

        notifications.Queue(
            task.OwnerId,
            NotificationType.TaskDue,
            task.Title,
            BodyFor(task, QuietHours.Resolve(user?.TimeZone)),
            route: $"/gorevler/{task.Id}",
            entityType: EntityType,
            entityId: task.Id,
            scheduledAt: task.ReminderAt);
    }

    /// <summary>
    /// Drops a reminder that has not gone out yet.
    /// </summary>
    /// <remarks>
    /// Only rows that are still purely pending are removed. Once the outbox has
    /// claimed one (<c>queued_at</c>) or delivered it (<c>sent_at</c>), it stays:
    /// a notification the user has already seen is part of their inbox, not a
    /// draft to retract.
    /// </remarks>
    public async Task CancelAsync(Guid taskId, CancellationToken ct)
    {
        await db.Notifications
            .Where(n => n.EntityType == EntityType
                        && n.EntityId == taskId
                        && n.SentAt == null
                        && n.QueuedAt == null)
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// What the notification says under the task's title.
    /// </summary>
    /// <remarks>
    /// Rendered in the user's own zone. A deadline shown in UTC to someone in
    /// Istanbul is three hours wrong, which for a "due at 09:55" reminder is the
    /// difference between useful and actively misleading.
    /// </remarks>
    private static string BodyFor(TaskItem task, TimeZoneInfo zone)
    {
        if (task.DueAt is null) return "Hatırlatma zamanı geldi.";

        var due = TimeZoneInfo.ConvertTime(task.DueAt.Value, zone);
        var reminder = TimeZoneInfo.ConvertTime(task.ReminderAt!.Value, zone);

        var sameDay = due.Date == reminder.Date;
        var tomorrow = due.Date == reminder.Date.AddDays(1);

        return sameDay
            ? $"Bugün {due:HH:mm}'de teslim."
            : tomorrow
                ? $"Yarın {due:HH:mm}'de teslim."
                : $"{due:dd.MM.yyyy HH:mm}'de teslim.";
    }
}
