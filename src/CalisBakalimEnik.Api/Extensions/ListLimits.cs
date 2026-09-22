namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// The ceiling on a list endpoint that has no paging of its own.
/// </summary>
/// <remarks>
/// <para>Several collections are bounded by human behaviour rather than by
/// code: courses, projects, medications, workout plans, terms, timetable
/// entries, devices, sessions. Nobody has four hundred courses, so these
/// endpoints were written without a <c>Take</c> at all.</para>
///
/// <para>"Nobody would" stops being a bound the moment the service is open to
/// the public. An account that creates a hundred thousand timetable entries
/// turns <c>GET /schedule</c> into a query that reads them all, serialises them
/// all, and does it again on every request — a cheap way to make the service
/// expensive for everyone else. This is that floor: <b>abuse protection, not
/// pagination.</b></para>
///
/// <para>It is deliberately far above any real use, so no genuine account ever
/// meets it. An account that does has something wrong with it, and the fix for
/// that account is a cursor, not a bigger number.</para>
/// </remarks>
public static class ListLimits
{
    public const int Ceiling = 500;
}
