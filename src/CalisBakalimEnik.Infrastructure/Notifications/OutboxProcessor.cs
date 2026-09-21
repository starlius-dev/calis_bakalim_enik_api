using System.Text.Json;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Notifications;

/// <summary>
/// Drains the transactional outbox.
/// </summary>
/// <remarks>
/// Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c> inside a transaction, so
/// two API instances can run this at once without double-sending and without
/// blocking each other. Failures back off exponentially rather than spinning.
/// See docs/NOTIFICATIONS.md §2.
/// </remarks>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopes,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    private const int BatchSize = 20;
    private const int MaxAttempts = 8;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the app finish starting before the first poll: a migration may
        // still be running, and a failed query here would log a scary error
        // about a table that is about to exist.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            try
            {
                await DrainAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // Never let one bad batch kill the service: it would stop every
                // notification until the next deploy.
                logger.LogError(e, "Outbox drain failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        // The context is configured with EnableRetryOnFailure, and that
        // execution strategy REFUSES a user-initiated transaction unless the
        // whole unit runs inside it. Without this the drain threw on every
        // single pass — and nothing else noticed, because notifications still
        // appeared in the inbox: only their delivery stopped.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(() => DrainOnceAsync(db, dispatcher, clock, ct));
    }

    private async Task DrainOnceAsync(
        AppDbContext db,
        NotificationDispatcher dispatcher,
        IClock clock,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var now = clock.UtcNow;

        // SKIP LOCKED is the point: a row another instance is working on is
        // passed over rather than waited for.
        var claimed = await db.OutboxMessages
            .FromSqlRaw(
                """
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL
                  AND (next_attempt_at IS NULL OR next_attempt_at <= {0})
                ORDER BY occurred_at
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
                """,
                now, BatchSize)
            .ToListAsync(ct);

        if (claimed.Count == 0)
        {
            await transaction.RollbackAsync(ct);
            return;
        }

        foreach (var message in claimed)
        {
            try
            {
                var outcome = await dispatcher.DispatchAsync(message, ct);

                if (outcome == DispatchOutcome.Done)
                {
                    message.ProcessedAt = clock.UtcNow;
                    message.Error = null;
                }

                // Deferred: the dispatcher moved NextAttemptAt to the end of the
                // user's quiet hours. The row stays unprocessed and must NOT be
                // counted as a failed attempt, or a long quiet window would
                // exhaust the retry budget and the message would be abandoned
                // before it was ever allowed to go.
            }
            catch (Exception e)
            {
                message.Attempts++;
                message.Error = e.Message.Length > 900 ? e.Message[..900] : e.Message;

                if (message.Attempts >= MaxAttempts)
                {
                    // Give up loudly. A row retried forever hides the failure
                    // and keeps the table growing.
                    message.ProcessedAt = clock.UtcNow;
                    logger.LogError(e,
                        "Outbox message {Id} abandoned after {Attempts} attempts",
                        message.Id, message.Attempts);
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(Math.Pow(3, message.Attempts));
                    message.NextAttemptAt = clock.UtcNow.Add(backoff);

                    logger.LogWarning(e,
                        "Outbox message {Id} failed, retry {Attempt} in {Backoff}",
                        message.Id, message.Attempts, backoff);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}

public enum DispatchOutcome
{
    /// <summary>Delivered, or there was nothing left to deliver.</summary>
    Done,

    /// <summary>Held until the user's quiet hours end. Not a failure.</summary>
    Deferred,
}

/// <summary>
/// Turns one outbox row into actual sends, and records what happened to each.
/// </summary>
public sealed class NotificationDispatcher(
    AppDbContext db,
    IPushSender push,
    IEmailSender email,
    IClock clock,
    ILogger<NotificationDispatcher> logger)
{
    public async Task<DispatchOutcome> DispatchAsync(
        OutboxMessage message, CancellationToken ct)
    {
        if (message.Type != NotificationService.OutboxType)
        {
            logger.LogWarning("Unknown outbox type {Type}", message.Type);
            return DispatchOutcome.Done;
        }

        var payload = JsonSerializer.Deserialize<DispatchPayload>(message.Payload)
                      ?? throw new InvalidOperationException("Unreadable outbox payload.");

        var notification = await db.Notifications
            .FirstOrDefaultAsync(n => n.Id == payload.NotificationId, ct);

        // Already gone — the user deleted it, or the row was cleaned up. Not an
        // error: nothing to deliver.
        if (notification is null) return DispatchOutcome.Done;
        if (notification.SentAt is not null) return DispatchOutcome.Done;

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == notification.UserId, ct);
        if (user is null) return DispatchOutcome.Done;

        var preference = await db.NotificationPreferences.FirstOrDefaultAsync(
            p => p.UserId == notification.UserId && p.Type == notification.Type, ct);

        var zone = QuietHours.Resolve(user.TimeZone);
        var now = clock.UtcNow;

        var allowedAt = QuietHours.NextAllowed(
            now, notification.Type, preference?.QuietFrom, preference?.QuietTo, zone);

        if (allowedAt > now)
        {
            // Rescheduled, not dropped. The row stays unprocessed and comes back
            // when the window ends.
            message.NextAttemptAt = allowedAt;

            logger.LogInformation(
                "Notification {Id} held for quiet hours until {At}",
                notification.Id, allowedAt);

            return DispatchOutcome.Deferred;
        }

        var pushWanted = preference?.Push ?? DefaultPush(notification.Type);
        var emailWanted = preference?.Email ?? DefaultEmail(notification.Type);

        if (pushWanted) await SendPushAsync(notification, ct);
        if (emailWanted) await SendEmailAsync(notification, user, ct);

        notification.SentAt = clock.UtcNow;
        return DispatchOutcome.Done;
    }

    private async Task SendPushAsync(Notification notification, CancellationToken ct)
    {
        var devices = await db.Devices
            .Where(d => d.UserId == notification.UserId
                        && d.PushEnabled
                        && d.FcmToken != null)
            .ToListAsync(ct);

        if (devices.Count == 0) return;

        var data = new Dictionary<string, string>
        {
            ["notificationId"] = notification.Id.ToString(),
            ["type"] = notification.Type.ToString(),
            // Lets the client discard a message for an account that has since
            // signed out on this device — tokens outlive sessions.
            ["userId"] = notification.UserId.ToString(),
        };

        if (RouteOf(notification) is { } route) data["route"] = route;

        foreach (var device in devices)
        {
            var result = await push.SendAsync(
                new PushMessage(
                    device.FcmToken!,
                    notification.Title,
                    notification.Body,
                    data,
                    AndroidChannelId: notification.Type == NotificationType.SecurityAlert
                        ? "alerts"
                        : "reminders"),
                ct);

            db.NotificationDeliveries.Add(new NotificationDelivery
            {
                NotificationId = notification.Id,
                DeviceId = device.Id,
                Channel = DeliveryChannel.Push,
                Status = result switch
                {
                    { TokenInvalid: true } => DeliveryStatus.TokenInvalid,
                    { Succeeded: true } => DeliveryStatus.Sent,
                    _ => DeliveryStatus.Failed,
                },
                ProviderMessageId = result.MessageId,
                Error = result.Error,
                AttemptedAt = clock.UtcNow,
            });

            if (result.TokenInvalid)
            {
                // Prune it here, not on some future sweep: every send to a dead
                // token is a wasted call and a delivery row that says nothing.
                device.FcmToken = null;
                logger.LogInformation("Cleared a dead FCM token on device {Device}", device.Id);
            }
        }
    }

    private async Task SendEmailAsync(
        Notification notification, AppUser user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user.Email)) return;

        var status = DeliveryStatus.Sent;
        string? error = null;

        try
        {
            await email.SendAsync(
                user.Email,
                notification.Title,
                notification.Body,
                ct);
        }
        catch (Exception e)
        {
            // Email failing must not lose the push that already went out, so
            // this is recorded rather than thrown.
            status = DeliveryStatus.Failed;
            error = e.Message;
            logger.LogWarning(e, "Notification email failed for {Id}", notification.Id);
        }

        db.NotificationDeliveries.Add(new NotificationDelivery
        {
            NotificationId = notification.Id,
            Channel = DeliveryChannel.Email,
            Status = status,
            Error = error,
            AttemptedAt = clock.UtcNow,
        });
    }

    private static string? RouteOf(Notification notification)
    {
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.Data);
            return data is not null && data.TryGetValue("route", out var route) ? route : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // docs/NOTIFICATIONS.md §7. Used when the user has never touched their
    // preferences, which is most users.
    private static bool DefaultPush(NotificationType type) => type != NotificationType.System;

    private static bool DefaultEmail(NotificationType type) =>
        type is NotificationType.ProjectDeadline or NotificationType.SecurityAlert;
}
