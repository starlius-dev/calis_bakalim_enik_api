using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// What day it is <b>for a given user</b>.
/// </summary>
/// <remarks>
/// Anything that defaults a <see cref="DateOnly"/> to "today" has to go through
/// here rather than reaching for <c>clock.UtcNow.UtcDateTime</c>. The UTC date
/// and the user's date are not the same day: at 00:30 in Istanbul it is still
/// yesterday in UTC, so a meal logged after midnight was filed under the
/// previous day and then missing from the screen that recorded it.
///
/// The offset is read at the user's current instant, so this stays right across
/// a DST change — the same reason
/// <see cref="Health.MedicationDoseService"/> resolves the zone per dose.
/// </remarks>
public static class UserDate
{
    /// <summary>The user's own calendar date right now.</summary>
    public static async Task<DateOnly> TodayAsync(
        AppDbContext db, Guid userId, IClock clock, CancellationToken ct)
    {
        var zone = await ZoneAsync(db, userId, ct);
        return Today(clock.UtcNow, zone);
    }

    /// <summary>
    /// The date an instant falls on in a zone. Pure, and the whole of the
    /// arithmetic — the database lookup above adds nothing testable.
    /// </summary>
    public static DateOnly Today(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);

    /// <summary>
    /// The user's stored zone, falling back to the app default when they have
    /// none — never to UTC, which is nobody's actual day.
    /// </summary>
    public static async Task<TimeZoneInfo> ZoneAsync(
        AppDbContext db, Guid userId, CancellationToken ct)
    {
        var id = await db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.TimeZone)
            .FirstOrDefaultAsync(ct);

        return QuietHours.Resolve(id);
    }
}
