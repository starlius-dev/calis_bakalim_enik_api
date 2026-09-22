using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Persistence;

/// <summary>
/// The retention sweep — docs/DATABASE.md §6.
/// </summary>
/// <remarks>
/// The statements themselves need a real schema and are exercised by running
/// the sweep with <c>Retention:RunOnStartup</c>. What is unit-testable is the
/// part that decides WHAT gets deleted and WHEN, which is also the part where a
/// mistake is expensive: a predicate that forgets its cutoff empties a table.
/// </remarks>
public class RetentionCleanupTests
{
    private static DateTimeOffset Utc(int hour, int minute = 0) =>
        new(2026, 9, 22, hour, minute, 0, TimeSpan.Zero);

    // ── scheduling ───────────────────────────────────────────────────

    [Fact]
    public void Before_the_hour_it_waits_until_today()
    {
        RetentionCleanup.UntilNextRun(Utc(1, 30), 3)
            .Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void After_the_hour_it_waits_until_tomorrow()
    {
        RetentionCleanup.UntilNextRun(Utc(4), 3)
            .Should().Be(TimeSpan.FromHours(23));
    }

    [Fact]
    public void Exactly_on_the_hour_it_waits_a_full_day_rather_than_nothing()
    {
        // A zero delay here would turn the loop into a sweep-per-tick for the
        // whole of 03:00.
        RetentionCleanup.UntilNextRun(Utc(3), 3)
            .Should().Be(TimeSpan.FromHours(24));
    }

    [Fact]
    public void Midnight_is_a_valid_hour()
    {
        RetentionCleanup.UntilNextRun(Utc(23, 30), 0)
            .Should().Be(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void The_delay_is_never_negative()
    {
        // Negative would throw out of Task.Delay and take the loop down.
        for (var hour = 0; hour < 24; hour++)
        for (var target = 0; target < 24; target++)
        {
            RetentionCleanup.UntilNextRun(Utc(hour, 17), target)
                .Should().BePositive($"at {hour}:17 targeting {target}:00");
        }
    }

    // ── what gets deleted ────────────────────────────────────────────

    [Fact]
    public void Every_rule_is_bounded_by_the_cutoff()
    {
        // The one that matters. A predicate without @cutoff matches every row
        // in the table, and the job would empty it on its first run — at three
        // in the morning, in batches, successfully.
        foreach (var rule in RetentionCleanup.Rules(new RetentionOptions()))
        {
            rule.Predicate.Should().Contain("@cutoff", $"'{rule.Name}' is unbounded");
        }
    }

    [Fact]
    public void The_shipped_windows_match_the_database_doc()
    {
        var options = new RetentionOptions();

        options.SecurityEventDays.Should().Be(365);
        options.RefreshTokenDays.Should().Be(90);
        options.ProcessedOutboxDays.Should().Be(7);
    }

    [Fact]
    public void Read_notifications_are_kept_unless_someone_asks()
    {
        // Not a log — a user's own inbox — and the database doc sets no policy
        // for it. 0 means never.
        new RetentionOptions().ReadNotificationDays.Should().Be(0);
    }

    [Fact]
    public void A_zero_window_is_never_rather_than_everything()
    {
        var options = new RetentionOptions
        {
            SecurityEventDays = 0,
            RefreshTokenDays = 0,
            ProcessedOutboxDays = 0,
        };

        // The sweep skips a rule whose window is 0. If that were read as "older
        // than now", a mistyped setting would delete the entire audit trail.
        RetentionCleanup.Rules(options).Should().OnlyContain(r => r.Days <= 0);
    }

    [Fact]
    public void A_dead_refresh_token_is_only_removed_once_it_has_also_expired()
    {
        var rule = RetentionCleanup.Rules(new RetentionOptions())
            .Single(r => r.Table == "refresh_tokens");

        // Reuse detection works by FINDING a revoked row: a rotated token
        // presented again is how a leaked chain is caught. Pruning on
        // revoked_at alone would delete that evidence while the token is still
        // inside its lifetime, and the replay would look like an unknown
        // token — refused, but with no family revocation and no security
        // event. Both conditions, never either.
        rule.Predicate.Should().Contain("expires_at < @cutoff");
        rule.Predicate.Should().Contain("AND");
        rule.Predicate.Should().NotContain("OR");
    }

    [Fact]
    public void Every_rule_names_a_table_and_a_reason()
    {
        foreach (var rule in RetentionCleanup.Rules(new RetentionOptions()))
        {
            rule.Table.Should().NotBeNullOrWhiteSpace();
            // The name goes in the log line an operator reads at 03:00.
            rule.Name.Should().NotBeNullOrWhiteSpace();
        }
    }
}
