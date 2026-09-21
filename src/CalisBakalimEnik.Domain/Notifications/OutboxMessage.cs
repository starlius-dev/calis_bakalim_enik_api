using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Notifications;

/// <summary>
/// The transactional outbox.
/// </summary>
/// <remarks>
/// The business change, the notification row and this row are written in ONE
/// transaction. A crash between "task saved" and "reminder sent" therefore
/// cannot leave them disagreeing, and FCM being unreachable delays delivery
/// instead of dropping it.
///
/// Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c>, so a second API
/// instance can be added later without double-sending.
/// See docs/DATABASE.md §7 and docs/NOTIFICATIONS.md §2.
/// </remarks>
public class OutboxMessage : BaseEntity
{
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }

    public short Attempts { get; set; }

    /// <summary>
    /// When this row becomes claimable again. Set forward on every failure, so
    /// a permanently failing message backs off instead of spinning.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? Error { get; set; }
}
