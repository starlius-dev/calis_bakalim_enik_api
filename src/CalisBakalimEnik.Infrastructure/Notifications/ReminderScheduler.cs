using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Notifications;
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

        foreach (var notification in due)
        {
            db.OutboxMessages.Add(NotificationService.Dispatch(notification, now));
            notification.QueuedAt = now;
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Queued {Count} due reminders", due.Count);
    }
}
