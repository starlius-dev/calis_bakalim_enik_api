using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Notifications;

public enum NotificationType : short
{
    TaskDue = 1,
    EventSoon = 2,
    ProjectDeadline = 3,
    MedicationDue = 4,
    WorkoutDue = 5,
    SecurityAlert = 6,
    System = 7,
}

public enum DeliveryChannel : short
{
    Push = 1,
    Email = 2,
    Sms = 3,
    InApp = 4,
}

public enum DeliveryStatus : short
{
    Pending = 1,
    Sent = 2,
    Failed = 3,

    /// <summary>
    /// The provider says the token is gone — reinstalled app, cleared data,
    /// revoked permission. The device row's token is cleared rather than
    /// retried, which is the only thing that stops a dead token being sent to
    /// forever.
    /// </summary>
    TokenInvalid = 4,
}

/// <summary>
/// The inbox record and the source of truth for a notification.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> an <see cref="OwnedEntity"/>. The ownership query
/// filter resolves the current user from the request, and the outbox processor
/// runs on a background scope with no request — every notification would be
/// invisible to the thing whose job is to send them. Ownership is enforced
/// explicitly on every endpoint instead, the same way refresh tokens are.
/// See docs/DATABASE.md §2 and §7.
/// </remarks>
public class Notification : BaseEntity
{
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }

    /// <summary>
    /// Rendered on a lock screen, so it never carries anything private:
    /// a medication reminder says "İlaç zamanı", not which medication.
    /// See docs/DATABASE.md §7.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// Carries <c>route</c>, the contract that lets the SERVER decide where a
    /// tap lands. A new notification type then needs no client release.
    /// </summary>
    public string Data { get; set; } = "{}";

    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }

    /// <summary>Null means "send now".</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>
    /// When the scheduler handed this to the outbox. Without it, a scheduled
    /// notification is eligible again on the very next pass — the row is not
    /// "sent" until the outbox actually delivers it, so a minute later the
    /// scheduler would queue a second copy.
    /// </summary>
    public DateTimeOffset? QueuedAt { get; set; }

    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One row per device and channel attempted. Separate from the notification so
/// one message can fan out to three devices and report each outcome — which is
/// what lets a <see cref="DeliveryStatus.TokenInvalid"/> prune a dead token.
/// </summary>
public class NotificationDelivery : BaseEntity
{
    public Guid NotificationId { get; set; }
    public Guid? DeviceId { get; set; }
    public DeliveryChannel Channel { get; set; }
    public DeliveryStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
}

/// <summary>
/// Per-user, per-type channel settings. Quiet hours are LOCAL times, evaluated
/// against the user's own time zone at send time.
/// </summary>
public class NotificationPreference
{
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public bool Push { get; set; } = true;
    public bool Email { get; set; }
    public bool InApp { get; set; } = true;
    public TimeOnly? QuietFrom { get; set; }
    public TimeOnly? QuietTo { get; set; }
}
