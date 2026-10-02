using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Health;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Notifications;

/// <summary>
/// Hands due reminders to the outbox.
/// </summary>
/// <remarks>
/// Reminders are stored as an absolute <c>timestamptz</c> computed at write
/// time from the user's local intent, so this loop never does time-zone
/// arithmetic — it only asks "is this instant in the past yet". The zone work
/// happens once, where the user expressed the intent. See NOTIFICATIONS.md §6.
///
/// A minute of granularity is deliberate: a reminder that fires at 09:55:03
/// instead of 09:55:00 is indistinguishable to a person, and a tighter loop is
/// a query per second forever.
/// </remarks>
public sealed class ReminderScheduler(
    IServiceScopeFactory scopes,
    ILogger<ReminderScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(8), ct);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await QueueDueAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Reminder scheduling pass failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task QueueDueAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var now = clock.UtcNow;

        var due = await db.Notifications
            .Where(n => n.ScheduledAt != null
                        && n.ScheduledAt <= now
                        && n.QueuedAt == null
                        && n.SentAt == null)
            .OrderBy(n => n.ScheduledAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (due.Count == 0) return;

        // A dose reminder whose dose was handled or no longer exists has
        // nothing to say (J65). Dropping it on marking the dose covers the
        // usual case; this covers everything else, including reminders
        // written before they carried the dose's id.
        var doseIds = due
            .Where(n => n.EntityType == MedicationDoseService.DoseEntity && n.EntityId != null)
            .Select(n => n.EntityId!.Value)
            .Distinct()
            .ToList();

        var statuses = doseIds.Count == 0
            ? new Dictionary<Guid, DoseStatus>()
            : await db.MedicationDoses.IgnoreQueryFilters()
                .Where(d => doseIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Status, ct);

        var legacy = doseIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.Medications.IgnoreQueryFilters()
                .Where(m => doseIds.Contains(m.Id) && m.DeletedAt == null)
                .Select(m => m.Id)
                .ToListAsync(ct)).ToHashSet();

        var queued = 0;
        var dropped = 0;

        foreach (var notification in due)
        {
            var isDoseReminder = notification.EntityType == MedicationDoseService.DoseEntity
                                 && notification.EntityId is not null;

            if (isDoseReminder && !ShouldSendDoseReminder(
                    statuses.TryGetValue(notification.EntityId!.Value, out var status) ? status : null,
                    legacy.Contains(notification.EntityId!.Value)))
            {
                db.Notifications.Remove(notification);
                dropped++;
                continue;
            }

            db.OutboxMessages.Add(NotificationService.Dispatch(notification, now));
            notification.QueuedAt = now;
            queued++;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Queued {Count} due reminders, dropped {Dropped} for doses already handled",
            queued, dropped);
    }

    /// <summary>
    /// Whether a due dose reminder still has something to say.
    /// </summary>
    /// <param name="status">The dose's status, or null when no such dose exists.</param>
    /// <param name="legacyMedication">
    /// The reminder's id is a live medication's: written before 30 Sep 2026,
    /// when reminders carried the medication id. Its dose cannot be told apart,
    /// so it is sent as it always was.
    /// </param>
    public static bool ShouldSendDoseReminder(DoseStatus? status, bool legacyMedication) =>
        status switch
        {
            DoseStatus.Pending => true,
            null => legacyMedication,
            _ => false,
        };
}
