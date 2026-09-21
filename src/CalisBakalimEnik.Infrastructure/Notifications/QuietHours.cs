using CalisBakalimEnik.Domain.Notifications;

namespace CalisBakalimEnik.Infrastructure.Notifications;

/// <summary>
/// Quiet-hours arithmetic, kept pure so it can be tested without a database, a
/// clock or FCM. This is where the bugs live: every one of them is a time-zone
/// or a midnight-wrap mistake. See docs/NOTIFICATIONS.md §6.
/// </summary>
public static class QuietHours
{
    /// <summary>
    /// When the message may be delivered — <paramref name="instant"/> itself if
    /// the user is not in a quiet window, otherwise the end of that window.
    /// </summary>
    /// <remarks>
    /// Suppressed notifications are <b>rescheduled, not dropped</b>: a reminder
    /// silently discarded at 23:05 is a reminder the user never gets.
    ///
    /// <see cref="NotificationType.SecurityAlert"/> ignores quiet hours
    /// entirely. A sign-in from an unknown device is not something to sit on
    /// until morning.
    /// </remarks>
    public static DateTimeOffset NextAllowed(
        DateTimeOffset instant,
        NotificationType type,
        TimeOnly? quietFrom,
        TimeOnly? quietTo,
        TimeZoneInfo zone)
    {
        if (type == NotificationType.SecurityAlert) return instant;
        if (quietFrom is null || quietTo is null) return instant;
        if (quietFrom == quietTo) return instant;

        var local = TimeZoneInfo.ConvertTime(instant, zone);
        var localTime = TimeOnly.FromDateTime(local.DateTime);

        if (!IsQuiet(localTime, quietFrom.Value, quietTo.Value)) return instant;

        // The end of the window, in the user's own day. When the window wraps
        // midnight (22:00–07:00) and it is currently before the end, the end is
        // TODAY; when it is after the start, the end is TOMORROW.
        var endDate = local.Date;
        if (quietFrom > quietTo && localTime >= quietFrom.Value)
            endDate = endDate.AddDays(1);

        var endLocal = endDate.Add(quietTo.Value.ToTimeSpan());

        // The offset is read at the TARGET instant, not the current one: a zone
        // with DST can change offset inside the window, and reusing the current
        // offset would land the message an hour out.
        var offset = zone.GetUtcOffset(
            new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)));

        return new DateTimeOffset(endLocal, offset);
    }

    /// <summary>
    /// Whether a local time falls inside the window. Windows that wrap midnight
    /// are the normal case here — 22:00 to 07:00 is what a person means by
    /// "quiet hours" — so the wrap is not an edge case to bolt on afterwards.
    /// </summary>
    public static bool IsQuiet(TimeOnly now, TimeOnly from, TimeOnly to) =>
        from <= to
            ? now >= from && now < to
            : now >= from || now < to;

    /// <summary>
    /// Resolves an IANA zone, falling back to Istanbul rather than throwing.
    /// </summary>
    /// <remarks>
    /// A bad stored zone must not stop a reminder: the user gets it at the
    /// wrong local hour, which is recoverable, instead of never, which is not.
    /// </remarks>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return Default;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return Default;
        }
    }

    private static TimeZoneInfo Default => Resolve("Europe/Istanbul", fallback: true);

    private static TimeZoneInfo Resolve(string id, bool fallback)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Windows without ICU, or a container with no tzdata.
            return TimeZoneInfo.CreateCustomTimeZone(
                "Europe/Istanbul", TimeSpan.FromHours(3), "Türkiye", "+03");
        }
    }
}
