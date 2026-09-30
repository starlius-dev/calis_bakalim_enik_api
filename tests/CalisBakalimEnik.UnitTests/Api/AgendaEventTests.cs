using CalisBakalimEnik.Api.Features.Plan;
using CalisBakalimEnik.Domain.Content;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// One-off events on the week page (J73), placed on the user's own day.
/// </summary>
public class AgendaEventTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static Event Exam(DateTimeOffset startsAt, DateTimeOffset? endsAt = null, bool allDay = false) => new()
    {
        Title = "Fizik finali",
        EventType = EventType.Exam,
        StartsAt = startsAt,
        EndsAt = endsAt,
        AllDay = allDay,
        Location = "B-204",
    };

    [Fact]
    public void An_event_lands_on_the_local_day_with_local_times()
    {
        var e = PlanEndpointsViews.ToAgendaEvent(
            Exam(new DateTimeOffset(2026, 10, 2, 7, 0, 0, TimeSpan.Zero),
                 new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero)),
            Istanbul);

        e.Date.Should().Be(new DateOnly(2026, 10, 2));
        e.StartsAt.Should().Be(new TimeOnly(10, 0));
        e.EndsAt.Should().Be(new TimeOnly(12, 0));
        e.Type.Should().Be("Exam");
        e.Location.Should().Be("B-204");
    }

    [Fact]
    public void Late_UTC_evening_is_already_tomorrow_in_Istanbul()
    {
        var e = PlanEndpointsViews.ToAgendaEvent(
            Exam(new DateTimeOffset(2026, 10, 2, 21, 30, 0, TimeSpan.Zero)), Istanbul);

        e.Date.Should().Be(new DateOnly(2026, 10, 3));
        e.StartsAt.Should().Be(new TimeOnly(0, 30));
        e.EndsAt.Should().BeNull();
    }

    [Fact]
    public void An_all_day_event_carries_no_times()
    {
        var e = PlanEndpointsViews.ToAgendaEvent(
            Exam(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero), allDay: true), Istanbul);

        e.AllDay.Should().BeTrue();
        e.StartsAt.Should().BeNull();
        e.EndsAt.Should().BeNull();
    }
}
