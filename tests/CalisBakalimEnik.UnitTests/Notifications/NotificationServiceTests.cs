using System.Text.Json;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.UnitTests.Notifications;

/// <summary>
/// Queuing rules. No database is touched: nothing here gets past the change
/// tracker, which is exactly the boundary under test.
/// </summary>
public class NotificationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid User = Guid.Parse("01a0c0ae-bae4-7096-9382-9474ff37dd24");

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoUser : ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => false;
    }

    private static AppDbContext Context()
    {
        // A provider is configured but never connected to: Add() and the change
        // tracker do not need a database.
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new NoUser());
    }

    [Fact]
    public void An_immediate_notification_is_queued_for_dispatch_in_the_same_unit()
    {
        using var db = Context();
        var service = new NotificationService(db, new FixedClock());

        var notification = service.Queue(
            User, NotificationType.SecurityAlert, "Başlık", "Gövde", route: "/hesap");

        // Both tracked, neither saved: the CALLER owns the transaction, which
        // is what makes the outbox guarantee hold.
        db.ChangeTracker.Entries<Notification>().Should().HaveCount(1);
        db.ChangeTracker.Entries<OutboxMessage>().Should().HaveCount(1);

        var outbox = db.ChangeTracker.Entries<OutboxMessage>().Single().Entity;
        var payload = JsonSerializer.Deserialize<DispatchPayload>(outbox.Payload);

        payload!.NotificationId.Should().Be(notification.Id);
        outbox.NextAttemptAt.Should().Be(Now, "an immediate notification is claimable at once");
    }

    [Fact]
    public void A_scheduled_reminder_gets_no_outbox_row_yet()
    {
        using var db = Context();
        var service = new NotificationService(db, new FixedClock());

        service.Queue(
            User, NotificationType.TaskDue, "Fizik ödevi", "Yarın teslim",
            scheduledAt: Now.AddHours(18));

        db.ChangeTracker.Entries<Notification>().Should().HaveCount(1);

        // The bug this guards: queuing a reminder for dispatch when it is
        // CREATED sends it immediately, which is the opposite of a reminder.
        // ReminderScheduler writes the outbox row when it comes due.
        db.ChangeTracker.Entries<OutboxMessage>().Should().BeEmpty();
    }

    [Fact]
    public void The_route_is_stored_as_the_data_contract_the_client_reads()
    {
        using var db = Context();
        var service = new NotificationService(db, new FixedClock());

        var notification = service.Queue(
            User, NotificationType.TaskDue, "Başlık", "Gövde", route: "/gorevler/42");

        JsonSerializer.Deserialize<Dictionary<string, string>>(notification.Data)
            .Should().ContainKey("route")
            .WhoseValue.Should().Be("/gorevler/42");
    }

    [Fact]
    public void Without_a_route_the_data_blob_is_still_valid_json()
    {
        using var db = Context();
        var service = new NotificationService(db, new FixedClock());

        var notification = service.Queue(
            User, NotificationType.System, "Başlık", "Gövde");

        // The column is jsonb: an empty string would be rejected by Postgres,
        // and null would make every reader null-check.
        notification.Data.Should().Be("{}");
    }
}
