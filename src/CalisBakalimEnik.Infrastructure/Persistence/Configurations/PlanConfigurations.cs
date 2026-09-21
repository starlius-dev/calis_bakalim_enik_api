using CalisBakalimEnik.Domain.Plan;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class TermConfiguration : IEntityTypeConfiguration<Term>
{
    public void Configure(EntityTypeBuilder<Term> builder)
    {
        builder.ToTable("terms", t => t.HasCheckConstraint(
            "ck_terms_span", "ends_on >= starts_on"));

        builder.Property(t => t.Name).HasMaxLength(120).IsRequired();

        // One current term per user, enforced here rather than in a handler:
        // "set this one current" is two writes, and a crash between them would
        // otherwise leave two.
        builder.HasIndex(t => t.OwnerId)
            .HasDatabaseName("ux_terms_current")
            .IsUnique()
            .HasFilter("is_current AND deleted_at IS NULL");
    }
}

public sealed class ScheduleEntryConfiguration : IEntityTypeConfiguration<ScheduleEntry>
{
    public void Configure(EntityTypeBuilder<ScheduleEntry> builder)
    {
        builder.ToTable("schedule_entries", t =>
        {
            t.HasCheckConstraint("ck_schedule_span", "ends_at > starts_at");
            t.HasCheckConstraint("ck_schedule_day", "day_of_week BETWEEN 1 AND 7");
        });

        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Location).HasMaxLength(200);
        builder.Property(e => e.RecurrenceRule).HasMaxLength(500);

        builder.Property(e => e.StartsAt).HasColumnType("time");
        builder.Property(e => e.EndsAt).HasColumnType("time");

        builder.HasIndex(e => new { e.OwnerId, e.DayOfWeek })
            .HasDatabaseName("ix_schedule_day")
            .HasFilter("deleted_at IS NULL");
    }
}

public sealed class FocusSessionConfiguration : IEntityTypeConfiguration<FocusSession>
{
    public void Configure(EntityTypeBuilder<FocusSession> builder)
    {
        builder.ToTable("focus_sessions", t => t.HasCheckConstraint(
            "ck_focus_seconds", "focus_seconds >= 0"));

        builder.HasIndex(s => new { s.OwnerId, s.StartedAt })
            .HasDatabaseName("ix_focus_owner_day")
            .IsDescending(false, true);

        // SET NULL: the session is a record of time spent and outlives the task
        // it was spent on. Cascading would quietly delete study history when a
        // finished task is tidied away.
        builder.HasOne<TaskItem>()
            .WithMany()
            .HasForeignKey(s => s.TaskId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
