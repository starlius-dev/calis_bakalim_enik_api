using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Plan;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Plan;

/// <summary>
/// Calendar arithmetic across time zones — pure, and exhaustively testable
/// without a database. See docs/API-SURFACE.md §9.
/// </summary>
public class AgendaExpanderTests
{
    private static readonly TimeZoneInfo Istanbul = QuietHours.Resolve("Europe/Istanbul");

    // 2026-09-21 is a Monday.
    private static readonly DateOnly Monday = new(2026, 9, 21);

    private static ScheduleEntry Entry(
        short day = 1,
        string title = "Fizik",
        DateOnly? from = null,
        DateOnly? to = null) => new()
    {
        Title = title,
        DayOfWeek = day,
        StartsAt = new TimeOnly(9, 55),
        EndsAt = new TimeOnly(11, 30),
        Location = "B-204",
        ValidFrom = from ?? new DateOnly(2026, 9, 1),
        ValidTo = to,
    };

    [Fact]
    public void A_weekly_entry_appears_once_per_week_in_the_window()
    {
        var occurrences = AgendaExpander.Expand(
            [Entry()], Monday, Monday.AddDays(20), Istanbul);

        occurrences.Should().HaveCount(3);
        occurrences.Select(o => o.Date)
            .Should().Equal(Monday, Monday.AddDays(7), Monday.AddDays(14));
    }

    [Fact]
    public void Sunday_is_seven_not_zero()
    {
        // .NET numbers Sunday as 0 and ISO-8601 as 7. Getting this wrong shifts
        // every Sunday entry onto Monday, or drops it entirely.
        var sunday = Monday.AddDays(6);

        var occurrences = AgendaExpander.Expand(
            [Entry(day: 7)], sunday, sunday, Istanbul);

        occurrences.Should().HaveCount(1);
        occurrences[0].Date.Should().Be(sunday);
    }

    [Fact]
    public void Validity_bounds_are_honoured_at_both_ends()
    {
        var entries = new[]
        {
            Entry(title: "started later", from: Monday.AddDays(7)),
            Entry(title: "already finished", to: Monday.AddDays(-1)),
            Entry(title: "runs throughout"),
        };

        var titles = AgendaExpander
            .Expand(entries, Monday, Monday, Istanbul)
            .Select(o => o.Title);

        titles.Should().Equal("runs throughout");
    }

    [Fact]
    public void Wall_clock_times_become_the_right_instant()
    {
        var occurrence = AgendaExpander
            .Expand([Entry()], Monday, Monday, Istanbul)
            .Single();

        // 09:55 in Istanbul is 06:55 UTC. The local time is what the screen
        // shows; the instant is what sorting and reminders use.
        occurrence.StartsAt.Should().Be(new TimeOnly(9, 55));
        occurrence.StartsAtUtc.Should().Be(
            new DateTimeOffset(2026, 9, 21, 6, 55, 0, TimeSpan.Zero));
        occurrence.EndsAtUtc.Should().Be(
            new DateTimeOffset(2026, 9, 21, 8, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void The_users_zone_decides_the_instant()
    {
        var istanbul = AgendaExpander.Expand([Entry()], Monday, Monday, Istanbul).Single();
        var london = AgendaExpander
            .Expand([Entry()], Monday, Monday, QuietHours.Resolve("Europe/London"))
            .Single();

        // Same wall clock, different moments. A server that expanded in its own
        // zone would put every lecture in the wrong hour for someone.
        istanbul.StartsAt.Should().Be(london.StartsAt);
        istanbul.StartsAtUtc.Should().NotBe(london.StartsAtUtc);
    }

    [Fact]
    public void The_window_is_capped_rather_than_trusted()
    {
        // An unbounded range is a free way to make the server do work. The cap
        // truncates instead of refusing, so a greedy client still gets an
        // answer — just a bounded one.
        var occurrences = AgendaExpander.Expand(
            [Entry()], Monday, Monday.AddYears(5), Istanbul);

        occurrences.Should().HaveCount(13, "90 days holds 13 Mondays");
        occurrences.Last().Date.Should()
            .BeOnOrBefore(Monday.AddDays(AgendaExpander.MaxWindowDays));
    }

    [Fact]
    public void Occurrences_come_back_in_chronological_order()
    {
        var entries = new[]
        {
            Entry(day: 3, title: "Çarşamba"),
            Entry(day: 1, title: "Pazartesi"),
            Entry(day: 5, title: "Cuma"),
        };

        AgendaExpander.Expand(entries, Monday, Monday.AddDays(6), Istanbul)
            .Select(o => o.Title)
            .Should().Equal("Pazartesi", "Çarşamba", "Cuma");
    }

    [Fact]
    public void A_backwards_window_yields_nothing_instead_of_throwing()
    {
        AgendaExpander.Expand([Entry()], Monday, Monday.AddDays(-7), Istanbul)
            .Should().BeEmpty();
    }
}
