using CalisBakalimEnik.Domain.Plan;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("tasks");

        builder.Property(t => t.Title).HasMaxLength(200).IsRequired();
        builder.Property(t => t.Notes).HasMaxLength(4000);
        builder.Property(t => t.Status).HasConversion<short>();
        builder.Property(t => t.Priority).HasConversion<short>();

        // Both partial on deleted_at: a soft-deleted task is dead weight in an
        // index that exists to answer "what is still open".
        builder.HasIndex(t => new { t.OwnerId, t.DueAt })
            .HasDatabaseName("ix_tasks_due")
            .HasFilter("deleted_at IS NULL");

        builder.HasIndex(t => new { t.OwnerId, t.Status })
            .HasDatabaseName("ix_tasks_open")
            .HasFilter("deleted_at IS NULL");

        // The scheduler's lookup when a reminder is moved or cancelled.
        builder.HasIndex(t => t.ReminderAt)
            .HasDatabaseName("ix_tasks_reminder")
            .HasFilter("reminder_at IS NOT NULL AND deleted_at IS NULL");

        builder.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(t => t.ParentTaskId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
