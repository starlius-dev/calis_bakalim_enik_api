using CalisBakalimEnik.Api.Features.Content;
using CalisBakalimEnik.Domain.Content;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// How a project's status, progress, dates and history relate (J82, J83).
/// </summary>
public class ProjectRulesTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    // ── status follows progress (J82) ────────────────────────────────────

    [Theory]
    [InlineData(ProjectStatus.InProgress)]
    [InlineData(ProjectStatus.Done)]
    public void Zero_percent_is_not_started_whatever_was_sent(ProjectStatus sent) =>
        ProjectRules.Normalise(sent, 0).Should().Be(ProjectStatus.NotStarted);

    [Fact]
    public void Progress_from_not_started_is_in_progress() =>
        ProjectRules.Normalise(ProjectStatus.NotStarted, 20).Should().Be(ProjectStatus.InProgress);

    [Fact]
    public void Done_below_a_full_bar_is_allowed() =>
        ProjectRules.Normalise(ProjectStatus.Done, 80).Should().Be(ProjectStatus.Done);

    // ── dates (J83) ──────────────────────────────────────────────────────

    [Fact]
    public void Starting_fills_the_start_date_once()
    {
        ProjectRules.Dates(ProjectStatus.InProgress, null, null, Today)
            .Should().Be(((DateOnly?)Today, (DateOnly?)null));

        var earlier = Today.AddDays(-10);
        ProjectRules.Dates(ProjectStatus.InProgress, earlier, null, Today).StartsOn
            .Should().Be(earlier, "a date already there, set or typed, is kept");
    }

    [Fact]
    public void Finishing_fills_the_finish_date_and_reopening_clears_it()
    {
        ProjectRules.Dates(ProjectStatus.Done, Today.AddDays(-3), null, Today).FinishedOn
            .Should().Be(Today);

        ProjectRules.Dates(ProjectStatus.InProgress, Today.AddDays(-3), Today, Today).FinishedOn
            .Should().BeNull();
    }

    [Fact]
    public void A_finish_date_the_person_entered_is_kept()
    {
        var typed = Today.AddDays(-1);
        ProjectRules.Dates(ProjectStatus.Done, null, typed, Today)
            .Should().Be(((DateOnly?)Today, (DateOnly?)typed));
    }

    [Fact]
    public void Not_started_sets_no_dates()
    {
        ProjectRules.Dates(ProjectStatus.NotStarted, null, null, Today)
            .Should().Be(((DateOnly?)null, (DateOnly?)null));
    }

    // ── history (J83) ────────────────────────────────────────────────────

    private static readonly ProjectSnapshot Before = new(
        "Tez", null, ProjectStatus.InProgress, 40, Today.AddDays(-5), null, null, null);

    [Fact]
    public void Nothing_changed_records_nothing() =>
        ProjectRules.Diff(Before, Before).Should().BeEmpty();

    [Fact]
    public void Every_changed_field_is_recorded_with_both_values()
    {
        var after = Before with
        {
            Name = "Tez taslağı",
            Status = ProjectStatus.Done,
            ProgressPct = 100,
            FinishedOn = Today,
        };

        ProjectRules.Diff(Before, after).Should().BeEquivalentTo(new[]
        {
            ("name", (string?)"Tez", (string?)"Tez taslağı"),
            ("status", "InProgress", "Done"),
            ("progress", "40", "100"),
            ("finishedOn", null, "2026-09-30"),
        });
    }
}
