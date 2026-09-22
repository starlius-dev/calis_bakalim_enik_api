using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Creates next month's partitions before anything needs them.
/// </summary>
/// <remarks>
/// <para>A range-partitioned table rejects a row whose month has no partition.
/// <c>security_events</c> gains a row on every login, so a partition that does
/// not exist is not a housekeeping problem — it is a service nobody can sign in
/// to. The schema carries a DEFAULT partition as well, which turns that outage
/// into rows quietly piling up somewhere they do not belong.</para>
///
/// <para>This job is what keeps the default empty, and <b>an error line from
/// here is the only warning that it is not</b>. Recovery is manual once rows
/// are in there: the default has to be detached, the rows moved into a real
/// partition, and the default reattached — Postgres will not let a new
/// partition claim a range the default already holds rows for.</para>
///
/// <para><b>The work happens in the database, not here.</b> The application
/// connects as a role that cannot issue DDL, which is a boundary worth keeping:
/// an injection anywhere becomes arbitrary schema access the moment the app can
/// CREATE. So the migration installs SECURITY DEFINER functions, each
/// allowlisted to one table, and this job only calls them. The first version of
/// this class issued the DDL itself and failed on every month with
/// <c>42501: permission denied for schema public</c>.</para>
///
/// <para>It runs at startup and twice a day. Startup matters most: a deploy is
/// the cheapest moment to find out the window is short.</para>
/// </remarks>
public sealed class PartitionMaintenance(
    IServiceScopeFactory scopes,
    IOptions<PartitionOptions> options,
    ILogger<PartitionMaintenance> logger) : BackgroundService
{
    private readonly PartitionOptions _options = options.Value;

    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            // A warning rather than information: with this off the window stops
            // rolling, and the default partition starts filling the moment the
            // last created month passes.
            logger.LogWarning("Partition maintenance is disabled.");
            return;
        }

        // After migrations and seeding, which is where the partitions this
        // would otherwise duplicate come from.
        await Task.Delay(TimeSpan.FromSeconds(15), ct);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await EnsureAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Partition maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var months = Math.Clamp(_options.MonthsAhead, 1, 120);

        foreach (var table in PartitionOptions.Partitioned)
        {
            // Both parameterised: the table name reaches the database as a
            // value, and the function refuses anything outside its allowlist.
            var created = await db.Database
                .SqlQueryRaw<int>(
                    """SELECT ensure_month_partitions({0}, {1}) AS "Value" """,
                    table, months)
                .SingleAsync(ct);

            if (created > 0)
                logger.LogInformation(
                    "Created {Count} partitions of {Table}.", created, table);

            var stray = await db.Database
                .SqlQueryRaw<long>(
                    """SELECT default_partition_rows({0}) AS "Value" """, table)
                .SingleAsync(ct);

            if (stray == 0)
            {
                logger.LogDebug("{Table}: the default partition is empty.", table);
                continue;
            }

            logger.LogError(
                "{Count} rows are in {Table}'s DEFAULT partition. Partition creation " +
                "fell behind, and the months involved can no longer be created until " +
                "those rows are moved out. See docs/DATABASE.md §6.",
                stray, table);
        }
    }
}
