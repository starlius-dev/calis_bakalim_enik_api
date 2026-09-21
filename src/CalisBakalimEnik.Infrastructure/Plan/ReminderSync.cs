using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Plan;

/// <summary>
/// Keeps a row's reminder and its notification in step, for any entity that
/// has one.
/// </summary>
/// <remarks>
/// Always "cancel, then re-queue if it still applies". Patching an existing
/// notification in place would mean reasoning about which of the moved,
/// cleared, completed and deleted cases got here; this way there is one path
/// and it is idempotent.
///
/// Runs inside the CALLER's transaction: the change and its reminder either
/// both land or neither does.
/// </remarks>
public sealed class ReminderSync(AppDbContext db, NotificationService notifications)
{
    public async Task SyncAsync(
        string entityType,
        Guid entityId,
        Guid ownerId,
        NotificationType type,
        string title,
        string body,
        string route,
        DateTimeOffset? reminderAt,
        CancellationToken ct)
    {
        await CancelAsync(entityType, entityId, ct);

        if (reminderAt is null) return;

        notifications.Queue(
            ownerId,
            type,
            title,
            body,
            route: route,
            entityType: entityType,
            entityId: entityId,
            scheduledAt: reminderAt);
    }

    /// <summary>
    /// Drops a reminder that has not gone out yet.
    /// </summary>
    /// <remarks>
    /// Only rows still purely pending are removed. Once the outbox has claimed
    /// one (<c>queued_at</c>) or delivered it (<c>sent_at</c>) it stays: a
    /// notification the user has already seen is part of their inbox, not a
    /// draft to retract.
    /// </remarks>
    public Task CancelAsync(string entityType, Guid entityId, CancellationToken ct) =>
        db.Notifications
            .Where(n => n.EntityType == entityType
                        && n.EntityId == entityId
                        && n.SentAt == null
                        && n.QueuedAt == null)
            .ExecuteDeleteAsync(ct);

    /// <summary>
    /// A deadline rendered in the user's own zone.
    /// </summary>
    /// <remarks>
    /// Shown in UTC to someone in Istanbul it is three hours wrong, which for a
    /// "due at 09:55" reminder is the difference between useful and actively
    /// misleading.
    /// </remarks>
    public static string DueBody(
        DateTimeOffset? due, DateTimeOffset reminder, TimeZoneInfo zone)
    {
        if (due is null) return "Hatırlatma zamanı geldi.";

        var at = TimeZoneInfo.ConvertTime(due.Value, zone);
        var from = TimeZoneInfo.ConvertTime(reminder, zone);

        if (at.Date == from.Date) return $"Bugün {at:HH:mm}'de.";
        if (at.Date == from.Date.AddDays(1)) return $"Yarın {at:HH:mm}'de.";

        return $"{at:dd.MM.yyyy HH:mm}'de.";
    }

    public async Task<TimeZoneInfo> ZoneOfAsync(Guid ownerId, CancellationToken ct)
    {
        var id = await db.Users
            .Where(u => u.Id == ownerId)
            .Select(u => u.TimeZone)
            .FirstOrDefaultAsync(ct);

        return QuietHours.Resolve(id);
    }
}
