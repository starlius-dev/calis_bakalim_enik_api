using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Notifications;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Identity;

/// <summary>
/// "Today" is the user's day, not UTC's.
/// </summary>
/// <remarks>
/// This existed as a bug: every Health endpoint that defaulted a date reached
/// for <c>clock.UtcNow.UtcDateTime</c>, so between midnight and the user's UTC
/// offset the server's today was the user's yesterday. A meal logged at 00:30
/// in Istanbul was filed under the previous day and then absent from the screen
/// that recorded it.
/// </remarks>
public class UserDateTests
{
    private static readonly TimeZoneInfo Istanbul = QuietHours.Resolve("Europe/Istanbul");

    [Fact]
    public void Just_after_midnight_local_is_the_new_day_even_though_utc_lags()
    {
        // 21:30 UTC on the 21st is 00:30 on the 22nd in Istanbul (UTC+3).
        var instant = new DateTimeOffset(2026, 9, 21, 21, 30, 0, TimeSpan.Zero);

        UserDate.Today(instant, Istanbul).Should().Be(new DateOnly(2026, 9, 22));
        DateOnly.FromDateTime(instant.UtcDateTime).Should().Be(new DateOnly(2026, 9, 21));
    }

    [Fact]
    public void Just_before_midnight_utc_is_still_the_same_local_day()
    {
        // 23:30 UTC on the 21st is 02:30 on the 22nd in Istanbul — the local
        // day has already turned, which is the point.
        var instant = new DateTimeOffset(2026, 9, 21, 23, 30, 0, TimeSpan.Zero);

        UserDate.Today(instant, Istanbul).Should().Be(new DateOnly(2026, 9, 22));
    }

    [Fact]
    public void Midday_agrees_with_utc()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

        UserDate.Today(instant, Istanbul).Should().Be(new DateOnly(2026, 9, 21));
    }

    [Fact]
    public void A_zone_behind_utc_can_still_be_on_the_previous_day()
    {
        // 02:00 UTC on the 22nd is 22:00 on the 21st in New York.
        var newYork = QuietHours.Resolve("America/New_York");
        var instant = new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);

        UserDate.Today(instant, newYork).Should().Be(new DateOnly(2026, 9, 21));
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_the_app_default_not_to_utc()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 21, 30, 0, TimeSpan.Zero);

        UserDate.Today(instant, QuietHours.Resolve(null))
            .Should().Be(new DateOnly(2026, 9, 22));
    }
}
