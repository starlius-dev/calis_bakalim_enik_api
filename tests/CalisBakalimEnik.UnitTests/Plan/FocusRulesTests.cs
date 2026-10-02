using CalisBakalimEnik.Api.Features.Plan;
using CalisBakalimEnik.Domain.Plan;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Plan;

/// <summary>
/// The Odak timer, run on the server (J76).
/// </summary>
public class FocusRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static FocusSession Started() => new() { StartedAt = T0 };

    [Fact]
    public void Running_time_is_wall_time()
    {
        FocusRules.ElapsedSeconds(Started(), T0.AddMinutes(25)).Should().Be(25 * 60);
    }

    [Fact]
    public void Paused_time_is_not_counted_and_the_clock_stands_still()
    {
        var s = Started();
        FocusRules.Pause(s, T0.AddMinutes(10));

        FocusRules.ElapsedSeconds(s, T0.AddMinutes(40)).Should().Be(10 * 60, "it is paused");

        FocusRules.Resume(s, T0.AddMinutes(40));
        FocusRules.ElapsedSeconds(s, T0.AddMinutes(45)).Should().Be(15 * 60);
        s.PausedSeconds.Should().Be(30 * 60);
    }

    [Fact]
    public void Pausing_twice_or_resuming_a_running_timer_changes_nothing()
    {
        var s = Started();
        FocusRules.Pause(s, T0.AddMinutes(5));
        FocusRules.Pause(s, T0.AddMinutes(9));
        s.PausedAt.Should().Be(T0.AddMinutes(5));

        var running = Started();
        FocusRules.Resume(running, T0.AddMinutes(5));
        running.PausedSeconds.Should().Be(0);
    }

    [Fact]
    public void Ending_records_the_time_and_one_block()
    {
        var s = Started();
        FocusRules.End(s, T0.AddMinutes(50));

        s.EndedAt.Should().Be(T0.AddMinutes(50));
        s.FocusSeconds.Should().Be(50 * 60);
        s.DoneBlocks.Should().Be(1);
    }

    [Fact]
    public void Ending_while_paused_ends_at_the_pause()
    {
        var s = Started();
        FocusRules.Pause(s, T0.AddMinutes(20));
        FocusRules.End(s, T0.AddHours(3));

        s.EndedAt.Should().Be(T0.AddMinutes(20));
        s.FocusSeconds.Should().Be(20 * 60, "the pause is not focus");
    }

    [Fact]
    public void Ending_twice_does_not_stretch_it()
    {
        var s = Started();
        FocusRules.End(s, T0.AddMinutes(10));
        FocusRules.End(s, T0.AddMinutes(90));

        s.EndedAt.Should().Be(T0.AddMinutes(10));
    }

    [Fact]
    public void A_timer_left_running_stops_itself_at_24_hours()
    {
        var s = Started();
        FocusRules.ExpireIfOverCap(s, T0.AddHours(23)).Should().BeFalse();

        FocusRules.ExpireIfOverCap(s, T0.AddHours(30)).Should().BeTrue();
        s.EndedAt.Should().Be(T0.AddHours(24), "it ends when it reached the cap, not when noticed");
        s.FocusSeconds.Should().Be(24 * 3600);
        s.DoneBlocks.Should().Be(1);
    }

    [Fact]
    public void The_cap_counts_focus_so_pauses_push_it_later()
    {
        var s = Started();
        FocusRules.Pause(s, T0.AddHours(1));
        FocusRules.Resume(s, T0.AddHours(3));

        FocusRules.ExpireIfOverCap(s, T0.AddHours(25)).Should().BeFalse();
        FocusRules.ExpireIfOverCap(s, T0.AddHours(27)).Should().BeTrue();
        s.EndedAt.Should().Be(T0.AddHours(26));
    }

    [Fact]
    public void A_paused_timer_never_expires()
    {
        var s = Started();
        FocusRules.Pause(s, T0.AddHours(2));

        FocusRules.ExpireIfOverCap(s, T0.AddDays(3)).Should().BeFalse();
    }

    [Fact]
    public void The_counter_keeps_yesterday_while_a_timer_from_yesterday_runs()
    {
        var today = new DateOnly(2026, 10, 1);

        FocusRules.CounterDay(today, null).Should().Be(today);
        FocusRules.CounterDay(today, today).Should().Be(today);
        FocusRules.CounterDay(today, today.AddDays(-1)).Should().Be(today.AddDays(-1));
    }
}
