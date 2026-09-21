using CalisBakalimEnik.Domain.Content;
using CalisBakalimEnik.Domain.Plan;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class CourseConfiguration : IEntityTypeConfiguration<Course>
{
    public void Configure(EntityTypeBuilder<Course> builder)
    {
        builder.ToTable("courses");

        builder.Property(c => c.Name).HasMaxLength(120).IsRequired();
        builder.Property(c => c.Code).HasMaxLength(8);
        builder.Property(c => c.Tint).HasMaxLength(9).IsRequired();
        builder.Property(c => c.Instructor).HasMaxLength(120);

        builder.HasIndex(c => new { c.OwnerId, c.ArchivedAt })
            .HasDatabaseName("ix_courses_active")
            .HasFilter("deleted_at IS NULL");

        // SET NULL: archiving or deleting a term must not take its courses —
        // and their tasks and notes — with it.
        builder.HasOne<Term>()
            .WithMany()
            .HasForeignKey(c => c.TermId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("projects", t => t.HasCheckConstraint(
            "ck_projects_progress", "progress_pct BETWEEN 0 AND 100"));

        builder.Property(p => p.Name).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(4000);
        builder.Property(p => p.Status).HasConversion<short>();

        builder.HasIndex(p => new { p.OwnerId, p.DueOn })
            .HasDatabaseName("ix_projects_due")
            .HasFilter("deleted_at IS NULL");

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(p => p.CourseId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class EventConfiguration : IEntityTypeConfiguration<Event>
{
    public void Configure(EntityTypeBuilder<Event> builder)
    {
        builder.ToTable("events");

        builder.Property(e => e.Title).HasMaxLength(200).IsRequired();
        builder.Property(e => e.EventType).HasConversion<short>();
        builder.Property(e => e.Location).HasMaxLength(200);
        builder.Property(e => e.RecurrenceRule).HasMaxLength(500);

        builder.HasIndex(e => new { e.OwnerId, e.StartsAt })
            .HasDatabaseName("ix_events_window")
            .HasFilter("deleted_at IS NULL");

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(e => e.CourseId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.ToTable("notes");

        builder.Property(n => n.Title).HasMaxLength(200);
        builder.Property(n => n.Body).IsRequired();

        // Pinned notes are what the screen opens with, so they get their own
        // partial index rather than a scan of everything the user ever wrote.
        builder.HasIndex(n => n.OwnerId)
            .HasDatabaseName("ix_notes_pinned")
            .HasFilter("pinned AND deleted_at IS NULL");

        builder.HasIndex(n => new { n.OwnerId, n.CourseId })
            .HasDatabaseName("ix_notes_course")
            .HasFilter("deleted_at IS NULL");

        builder.HasOne<Course>()
            .WithMany()
            .HasForeignKey(n => n.CourseId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
