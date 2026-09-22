using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// One table's retention rule.
/// </summary>
/// <param name="Table">A literal from this file. Never user input — it is
/// interpolated into SQL, which is only safe because of that.</param>
/// <param name="Predicate">What makes a row prunable, in terms of
/// <c>@cutoff</c>.</param>
public readonly record struct RetentionRule(string Name, string Table, string Predicate, int Days);

/// <summary>
/// Deletes what has aged out — docs/DATABASE.md §6.
/// </summary>
/// <remarks>
/// Four tables grow with use and nothing ever removed from them:
/// <c>security_events</c> gains a row on every login and every token refresh,
/// <c>outbox_messages</c> one per notification ever sent, <c>refresh_tokens</c>
/// one per rotation — which is one every fifteen minutes per active session —
/// and <c>notifications</c> one per reminder. On a single-user planner none of
/// these is urgent; left alone for a year they are still the only tables whose
/// size depends on time rather than on what the user typed.
///
/// Deletes run in batches. A single statement covering a year of history holds
/// a lock for the whole of it and writes one enormous WAL record; a loop of
/// small deletes lets other work through in between and can be interrupted by
/// a shutdown without losing progress, because each batch is its own
/// transaction.
///
/// The sweep is idempotent and safe to run twice — it deletes by age, so a
/// second pass finds nothing. That matters because there is no leader election
/// here: if a second instance is ever added, both will run this.
/// </remarks>
public sealed class RetentionCleanup(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    IClock clock,
    ILogger<RetentionCleanup> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    /// <summary>
    /// A stop so a bug in the predicate cannot spin forever. At the default
    /// batch size this is fifty million rows, which is far more than this
    /// application can produce in a year and far less than an infinite loop.
    /// </summary>
    private const int MaxBatchesPerTable = 10_000;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Retention sweep is disabled.");
            return;
        }

        if (_options.RunOnStartup)
        {
            // After the rest of startup, so the first sweep does not compete
            // with migrations and seeding for the same connections.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
                logger.LogInformation("Running the retention sweep on startup.");
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Startup retention sweep failed.");
            }
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var delay = UntilNextRun(clock.UtcNow);
                logger.LogDebug("Next retention sweep in {Delay}.", delay);

                await Task.Delay(delay, ct);
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep must not take the loop down with it: the
                // tables would then grow forever with nothing in the log to
                // say why. Wait out the hour and try again.
                logger.LogError(ex, "Retention sweep failed.");

                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), ct);
                }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// How long until the next <see cref="RetentionOptions.RunAtHourUtc"/>.
    /// </summary>
    /// <remarks>
    /// Computed from the clock each time rather than counted in 24-hour steps
    /// from startup, so a restart does not shift the hour the sweep runs at and
    /// a service that has been up for months has not drifted into the middle of
    /// the day.
    /// </remarks>
    public static TimeSpan UntilNextRun(DateTimeOffset now, int hourUtc)
    {
        var today = new DateTimeOffset(
            now.UtcDateTime.Date.AddHours(hourUtc), TimeSpan.Zero);

        var next = today > now ? today : today.AddDays(1);
        return next - now;
    }

    private TimeSpan UntilNextRun(DateTimeOffset now) =>
        UntilNextRun(now, Math.Clamp(_options.RunAtHourUtc, 0, 23));

    /// <summary>The rules, in the order they run.</summary>
    /// <remarks>
    /// Notifications are last because the delete cascades into
    /// <c>notification_deliveries</c>, which is the largest write of the four
    /// and the one most worth leaving until the cheap work is done.
    /// </remarks>
    public static IReadOnlyList<RetentionRule> Rules(RetentionOptions o) =>
    [
        new("security events", "security_events",
            "occurred_at < @cutoff", o.SecurityEventDays),

        // Both conditions, not either: a revoked token with a future expiry is
        // exactly the row reuse detection needs to find. See RetentionOptions.
        new("refresh tokens", "refresh_tokens",
            "expires_at < @cutoff AND COALESCE(revoked_at, expires_at) < @cutoff",
            o.RefreshTokenDays),

        new("processed outbox messages", "outbox_messages",
            "processed_at IS NOT NULL AND processed_at < @cutoff",
            o.ProcessedOutboxDays),

        new("read notifications", "notifications",
            "read_at IS NOT NULL AND read_at < @cutoff",
            o.ReadNotificationDays),
    ];

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = clock.UtcNow;
        var total = 0;

        foreach (var rule in Rules(_options))
        {
            // 0 means never. See RetentionOptions: the failure mode of a
            // missing setting must be "kept", not "deleted".
            if (rule.Days <= 0) continue;

            var cutoff = now.AddDays(-rule.Days);

            // A partitioned table gives up a whole month at a time. Dropping a
            // partition unlinks a file; deleting its rows marks each one dead,
            // writes a WAL record per row, and leaves the space occupied until
            // VACUUM catches up. At the scale this table reaches with real
            // users that is the difference between a moment and an hour.
            if (PartitionOptions.Partitioned.Contains(rule.Table))
                await DropAgedPartitionsAsync(db, rule.Table, cutoff, ct);

            // Still swept afterwards, and not as a belt-and-braces gesture:
            // the drop only takes months that have aged out ENTIRELY, so the
            // boundary month and anything sitting in the default partition are
            // left, and those are exactly the rows a partition drop cannot
            // reach.
            var deleted = await PruneAsync(db, rule, cutoff, ct);
            total += deleted;

            if (deleted > 0)
                logger.LogInformation("Retention: removed {Count} {What}.", deleted, rule.Name);
        }

        if (total == 0) logger.LogDebug("Retention: nothing to remove.");
    }

    private async Task<int> PruneAsync(
        AppDbContext db, RetentionRule rule, DateTimeOffset cutoff, CancellationToken ct)
    {
        var batch = Math.Clamp(_options.BatchSize, 1, 50_000);

        // ctid rather than the primary key: it needs no assumption about what
        // the key is called or whether it is a single column, and it keeps the
        // subquery to an index scan plus a limit.
        //
        // The table and predicate are literals from Rules() and the batch size
        // is an int; the only value from outside this file is the cutoff, and
        // that is a parameter.
        var sql = $"""
            DELETE FROM {rule.Table}
            WHERE ctid IN (
                SELECT ctid FROM {rule.Table}
                WHERE {rule.Predicate}
                LIMIT {batch}
            )
            """;

        var total = 0;

        for (var i = 0; i < MaxBatchesPerTable; i++)
        {
            var removed = await db.Database.ExecuteSqlRawAsync(
                sql, [new Npgsql.NpgsqlParameter("cutoff", cutoff)], ct);

            total += removed;

            // A short batch means the table is drained. Stopping here rather
            // than on `removed == 0` saves one empty round trip per table
            // every night, which is the common case once the backlog is gone.
            if (removed < batch) break;

            if (ct.IsCancellationRequested) break;
        }

        return total;
    }

    /// <summary>
    /// Hands the aged months to the database, which is the only thing
    /// allowed to drop them.
    /// </summary>
    /// <remarks>
    /// <c>DROP TABLE</c> is DDL, and the application's role cannot issue
    /// DDL — deliberately. <c>drop_aged_partitions</c> is a SECURITY
    /// DEFINER function installed by the migration, allowlisted to one
    /// table, which works out for itself which partitions have aged out.
    /// Both arguments are parameters.
    ///
    /// It only takes months that have ended entirely, and it never touches
    /// the DEFAULT partition, whose rows belong to no particular month.
    /// Those are left to the batched delete that runs next.
    /// </remarks>
    private async Task DropAgedPartitionsAsync(
        AppDbContext db, string table, DateTimeOffset cutoff, CancellationToken ct)
    {
        var dropped = await db.Database
            .SqlQueryRaw<int>(
                """SELECT drop_aged_partitions({0}, {1}) AS "Value" """,
                table, cutoff)
            .SingleAsync(ct);

        if (dropped > 0)
            logger.LogInformation(
                "Retention: dropped {Count} aged partitions of {Table}.",
                dropped, table);
    }
}
