using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Health;

/// <summary>
/// Keeps the dose horizon full and sweeps the ones that passed.
/// </summary>
/// <remarks>
/// The safety net, not the primary path: the endpoints generate doses the
/// moment a medication is added or changed, so a user never waits on this loop
/// to see today. What it adds is the rolling horizon — a daily medication
/// added a month ago still has doses two weeks out — and marking missed ones.
/// </remarks>
public sealed class MedicationDoseGenerator(
    IServiceScopeFactory scopes,
    ILogger<MedicationDoseGenerator> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(12), ct);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Medication dose generation failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var doses = scope.ServiceProvider.GetRequiredService<MedicationDoseService>();

        // IgnoreQueryFilters is required and deliberate: this runs on a
        // background scope with no current user, so the ownership filter would
        // match nothing. Each dose is written with its medication's own
        // OwnerId. See docs/ROADMAP.md risks.
        var medications = await db.Medications
            .IgnoreQueryFilters()
            .Where(m => m.DeletedAt == null
                        && m.PausedAt == null
                        && m.Frequency != MedicationFrequency.AsNeeded)
            .ToListAsync(ct);

        var created = 0;

        foreach (var medication in medications)
        {
            var zone = await doses.ZoneOfAsync(medication.OwnerId, ct);
            created += await doses.GenerateAsync(medication, zone, ct);
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Extended the dose horizon by {Count} doses", created);
        }

        var missed = await doses.MarkMissedAsync(ct);
        if (missed > 0) logger.LogInformation("Marked {Count} doses missed", missed);
    }
}
