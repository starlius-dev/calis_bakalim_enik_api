using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Notifications;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Notifications;

/// <summary>
/// The scheduler's time arithmetic, which is where the bugs are. None of this
/// needs a database, a clock or FCM. See docs/NOTIFICATIONS.md §9.
/// </summary>
public class QuietHoursTests
{
    private static readonly TimeZoneInfo Istanbul = QuietHours.Resolve("Europe/Istanbul");

    private static readonly TimeOnly TenPm = new(22, 0);
    private static readonly TimeOnly SevenAm = new(7, 0);

    [Theory]
    [InlineData(23, 30, true)]   // inside, after midnight-wrap start
    [InlineData(2, 0, true)]     // inside, past midnight
    [InlineData(6, 59, true)]    // inside, one minute before the end
    [InlineData(7, 0, false)]    // the end is exclusive
    [InlineData(21, 59, false)]  // one minute before the start
    [InlineData(22, 0, true)]    // the start is inclusive
    public void A_window_that_wraps_midnight_is_the_normal_case(
        int hour, int minute, bool expected)
    {
        QuietHours.IsQuiet(new TimeOnly(hour, minute), TenPm, SevenAm)
            .Should().Be(expected);
    }

    [Theory]
    [InlineData(13, 30, true)]
    [InlineData(12, 59, false)]
    [InlineData(15, 0, false)]
    public void A_window_inside_one_day_also_works(int hour, int minute, bool expected)
    {
        QuietHours.IsQuiet(new TimeOnly(hour, minute), new TimeOnly(13, 0), new TimeOnly(15, 0))
            .Should().Be(expected);
    }

    [Fact]
    public void Outside_quiet_hours_a_notification_goes_immediately()
    {
        // 12:00 local in Istanbul.
        var noon = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

        QuietHours.NextAllowed(noon, NotificationType.TaskDue, TenPm, SevenAm, Istanbul)
            .Should().Be(noon);
    }

    [Fact]
    public void Inside_quiet_hours_it_is_moved_to_the_end_of_the_window()
    {
        // 23:30 local on the 21st → 07:00 local on the 22nd.
        var lateEvening = new DateTimeOffset(2026, 9, 21, 20, 30, 0, TimeSpan.Zero);

        var allowed = QuietHours.NextAllowed(
            lateEvening, NotificationType.TaskDue, TenPm, SevenAm, Istanbul);

        var local = TimeZoneInfo.ConvertTime(allowed, Istanbul);

        local.Hour.Should().Be(7);
        local.Minute.Should().Be(0);
        local.Day.Should().Be(22, "the window started yesterday and ends tomorrow morning");
    }

    [Fact]
    public void Before_dawn_inside_the_window_it_waits_only_until_this_morning()
    {
        // 02:00 local on the 21st → 07:00 local the SAME day. Adding a day here
        // is the classic wrap bug: the user would get it 24 hours late.
        var smallHours = new DateTimeOffset(2026, 9, 20, 23, 0, 0, TimeSpan.Zero);

        var allowed = QuietHours.NextAllowed(
            smallHours, NotificationType.TaskDue, TenPm, SevenAm, Istanbul);

        var local = TimeZoneInfo.ConvertTime(allowed, Istanbul);

        local.Day.Should().Be(21);
        local.Hour.Should().Be(7);
    }

    [Fact]
    public void A_security_alert_ignores_quiet_hours()
    {
        // A sign-in from an unknown device is not something to sit on until
        // morning. docs/NOTIFICATIONS.md §6.
        var middleOfTheNight = new DateTimeOffset(2026, 9, 21, 0, 30, 0, TimeSpan.Zero);

        QuietHours.NextAllowed(
                middleOfTheNight, NotificationType.SecurityAlert, TenPm, SevenAm, Istanbul)
            .Should().Be(middleOfTheNight);
    }

    [Fact]
    public void No_window_configured_means_no_delay()
    {
        var instant = new DateTimeOffset(2026, 9, 21, 0, 30, 0, TimeSpan.Zero);

        QuietHours.NextAllowed(instant, NotificationType.TaskDue, null, null, Istanbul)
            .Should().Be(instant);

        // from == to is an empty window, not a 24-hour one. Read the other way,
        // a user who set both to 22:00 would never receive anything again.
        QuietHours.NextAllowed(instant, NotificationType.TaskDue, TenPm, TenPm, Istanbul)
            .Should().Be(instant);
    }

    [Fact]
    public void The_users_own_zone_decides_when_the_window_is()
    {
        // 23:30 UTC is 02:30 in Istanbul — inside quiet hours there, and well
        // outside them in London. Evaluating in server time would silence the
        // London user's evening and wake the Istanbul one.
        var instant = new DateTimeOffset(2026, 9, 21, 20, 30, 0, TimeSpan.Zero);
        var london = QuietHours.Resolve("Europe/London");

        QuietHours.NextAllowed(instant, NotificationType.TaskDue, TenPm, SevenAm, Istanbul)
            .Should().BeAfter(instant, "23:30 in Istanbul is inside the window");

        QuietHours.NextAllowed(instant, NotificationType.TaskDue, TenPm, SevenAm, london)
            .Should().Be(instant, "21:30 in London is not");
    }

    [Fact]
    public void An_unknown_time_zone_falls_back_rather_than_throwing()
    {
        // A bad stored zone must not stop a reminder: the wrong local hour is
        // recoverable, never arriving is not.
        var zone = QuietHours.Resolve("Mars/Olympus_Mons");

        zone.Should().NotBeNull();
        zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(3));
    }
}
