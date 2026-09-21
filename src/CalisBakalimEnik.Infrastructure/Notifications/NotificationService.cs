using System.Text.Json;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;

namespace CalisBakalimEnik.Infrastructure.Notifications;

/// <summary>
/// Creates notifications. The inbox row and its outbox row are written in the
/// SAME transaction as whatever caused them, which is the whole point of the
/// outbox: a crash between "the thing happened" and "the user was told" cannot
/// leave those two disagreeing.
/// </summary>
/// <remarks>
/// This deliberately does not call SaveChanges. The caller owns the
/// transaction — that is what makes the guarantee hold.
/// </remarks>
public sealed class NotificationService(AppDbContext db, IClock clock)
{
    public const string OutboxType = "notification.dispatch";

    /// <summary>
    /// Queues a notification. Pass <paramref name="scheduledAt"/> for a
    /// reminder; leave it null to send as soon as the outbox next runs.
    /// </summary>
    public Notification Queue(
        Guid userId,
        NotificationType type,
        string title,
        string body,
        string? route = null,
        string? entityType = null,
        Guid? entityId = null,
        DateTimeOffset? scheduledAt = null)
    {
        var now = clock.UtcNow;

        var notification = new Notification
        {
            UserId = userId,
            Type = type,
            Title = title,
            Body = body,
            Data = route is null
                ? "{}"
                : JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["route"] = route,
                }),
            EntityType = entityType,
            EntityId = entityId,
            ScheduledAt = scheduledAt,
            CreatedAt = now,
        };

        db.Notifications.Add(notification);

        // A scheduled notification gets no outbox row yet — ReminderScheduler
        // writes one when it comes due. Queuing it now would send it instantly,
        // which is the opposite of a reminder.
        if (scheduledAt is null) db.OutboxMessages.Add(Dispatch(notification, now));

        return notification;
    }

    internal static OutboxMessage Dispatch(Notification notification, DateTimeOffset now) =>
        new()
        {
            Type = OutboxType,
            Payload = JsonSerializer.Serialize(new DispatchPayload(notification.Id)),
            OccurredAt = now,
            NextAttemptAt = now,
        };
}

public sealed record DispatchPayload(Guid NotificationId);
