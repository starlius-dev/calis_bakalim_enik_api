using CalisBakalimEnik.Domain.Plan;

namespace CalisBakalimEnik.Infrastructure.Plan;

/// <param name="Date">The local calendar day this occurrence falls on.</param>
/// <param name="StartsAt">Wall-clock start, in the user's zone.</param>
/// <param name="StartsAtUtc">
/// The same moment as an instant, so a client can sort occurrences against
/// tasks and events without knowing the user's zone.
/// </param>
public sealed record AgendaOccurrence(
    Guid ScheduleEntryId,
    DateOnly Date,
    TimeOnly StartsAt,
    TimeOnly EndsAt,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    string Title,
    string? Location);

/// <summary>
/// Turns weekly timetable rows into the occurrences the Hafta screen draws.
/// </summary>
/// <remarks>
/// Pure, and deliberately so: this is calendar arithmetic across time zones,
/// which is where the bugs live, and it can be tested exhaustively without a
/// database. See docs/API-SURFACE.md §9.
/// </remarks>
public static class AgendaExpander
{
    /// <summary>
    /// The window is capped so an unbounded range cannot be used to burn CPU.
    /// </summary>
    public const int MaxWindowDays = 90;

    public static IReadOnlyList<AgendaOccurrence> Expand(
        IEnumerable<ScheduleEntry> entries,
        DateOnly from,
        DateOnly to,
        TimeZoneInfo zone)
    {
        if (to < from) return [];

        var occurrences = new List<AgendaOccurrence>();
        var last = from.AddDays(Math.Min(to.DayNumber - from.DayNumber, MaxWindowDays - 1));

        foreach (var entry in entries)
        {
            for (var day = from; day <= last; day = day.AddDays(1))
            {
                // ISO-8601 numbering: DayOfWeek.Sunday is 0 in .NET and 7 here,
                // which is the single easiest thing to get wrong in this file.
                var isoDay = (short)(day.DayOfWeek == System.DayOfWeek.Sunday
                    ? 7
                    : (int)day.DayOfWeek);

                if (entry.DayOfWeek != isoDay) continue;
                if (day < entry.ValidFrom) continue;
                if (entry.ValidTo is not null && day > entry.ValidTo) continue;

                occurrences.Add(new AgendaOccurrence(
                    entry.Id,
                    day,
                    entry.StartsAt,
                    entry.EndsAt,
                    ToInstant(day, entry.StartsAt, zone),
                    ToInstant(day, entry.EndsAt, zone),
                    entry.Title,
                    entry.Location));
            }
        }

        return occurrences
            .OrderBy(o => o.StartsAtUtc)
            .ThenBy(o => o.Title)
            .ToList();
    }

    /// <summary>
    /// A local date and wall-clock time as an instant in the user's zone.
    /// </summary>
    /// <remarks>
    /// The offset is read AT that local time, not at "now": in a zone with DST
    /// a lecture two months out sits on the other side of a transition, and
    /// reusing today's offset would place it an hour wrong. Turkey has no DST,
    /// but the user's zone is theirs to change.
    /// </remarks>
    private static DateTimeOffset ToInstant(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);

        // A time that does not exist (the hour DST skips) is nudged forward
        // rather than throwing: a timetable entry is not worth a 500.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);

        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
