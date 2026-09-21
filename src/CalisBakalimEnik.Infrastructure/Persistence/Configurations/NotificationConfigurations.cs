using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.Property(n => n.Type).HasConversion<short>();
        builder.Property(n => n.Title).HasMaxLength(160).IsRequired();
        builder.Property(n => n.Body).HasMaxLength(500).IsRequired();
        builder.Property(n => n.EntityType).HasMaxLength(64);

        builder.Property(n => n.Data)
            .HasColumnType("jsonb")
            .HasDefaultValueSql("'{}'::jsonb")
            .IsRequired();

        builder.HasIndex(n => new { n.UserId, n.CreatedAt })
            .HasDatabaseName("ix_notifications_inbox")
            .IsDescending(false, true);

        // Partial: the unread badge is read on every app open, and the index
        // only has to cover the rows that can contribute to it.
        builder.HasIndex(n => n.UserId)
            .HasDatabaseName("ix_notifications_unread")
            .HasFilter("read_at IS NULL");

        // The scheduler's only query. Partial for the same reason: everything
        // already sent is dead weight in it.
        builder.HasIndex(n => n.ScheduledAt)
            .HasDatabaseName("ix_notifications_due")
            .HasFilter("sent_at IS NULL");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(n => n.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class NotificationDeliveryConfiguration
    : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("notification_deliveries");

        builder.Property(d => d.Channel).HasConversion<short>();
        builder.Property(d => d.Status).HasConversion<short>();
        builder.Property(d => d.ProviderMessageId).HasMaxLength(200);
        builder.Property(d => d.Error).HasMaxLength(500);

        builder.HasIndex(d => d.NotificationId);

        builder.HasOne<Notification>()
            .WithMany()
            .HasForeignKey(d => d.NotificationId)
            .OnDelete(DeleteBehavior.Cascade);

        // SET NULL, not cascade: the delivery record is the evidence of what
        // happened, and it outlives the device it was sent to.
        builder.HasOne<Device>()
            .WithMany()
            .HasForeignKey(d => d.DeviceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class NotificationPreferenceConfiguration
    : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences");

        builder.HasKey(p => new { p.UserId, p.Type });
        builder.Property(p => p.Type).HasConversion<short>();

        // `time`, not `timestamptz`: quiet hours are a wall-clock intent in the
        // user's own zone. Storing them as instants would move them whenever
        // the user travels, which is the opposite of what they asked for.
        builder.Property(p => p.QuietFrom).HasColumnType("time");
        builder.Property(p => p.QuietTo).HasColumnType("time");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.Property(m => m.Type).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(m => m.Error).HasMaxLength(1000);

        builder.HasIndex(m => m.NextAttemptAt)
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("processed_at IS NULL");
    }
}
