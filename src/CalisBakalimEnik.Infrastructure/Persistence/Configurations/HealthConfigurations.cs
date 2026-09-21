using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CalisBakalimEnik.Infrastructure.Persistence.Configurations;

public sealed class MedicationConfiguration : IEntityTypeConfiguration<Medication>
{
    public void Configure(EntityTypeBuilder<Medication> builder)
    {
        builder.ToTable("medications");

        builder.Property(m => m.Name).HasMaxLength(200).IsRequired();
        builder.Property(m => m.Dose).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Instructions).HasMaxLength(500);
        builder.Property(m => m.StockUnit).HasMaxLength(40);
        builder.Property(m => m.Frequency).HasConversion<short>();

        builder.HasIndex(m => new { m.OwnerId, m.PausedAt })
            .HasDatabaseName("ix_medications_active")
            .HasFilter("deleted_at IS NULL");
    }
}

public sealed class MedicationTimeConfiguration : IEntityTypeConfiguration<MedicationTime>
{
    public void Configure(EntityTypeBuilder<MedicationTime> builder)
    {
        builder.ToTable("medication_times");

        builder.Property(t => t.TimeOfDay).HasColumnType("time");

        builder.HasOne<Medication>()
            .WithMany()
            .HasForeignKey(t => t.MedicationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.MedicationId);
    }
}

public sealed class MedicationDoseConfiguration : IEntityTypeConfiguration<MedicationDose>
{
    public void Configure(EntityTypeBuilder<MedicationDose> builder)
    {
        builder.ToTable("medication_doses");

        builder.Property(d => d.Status).HasConversion<short>();

        // What makes the generator idempotent: running it twice cannot create
        // a second dose for the same medication and moment.
        builder.HasIndex(d => new { d.MedicationId, d.ScheduledAt })
            .HasDatabaseName("uq_dose")
            .IsUnique();

        // The "what is due" query, and nothing else — so it only indexes the
        // rows that can still be due.
        builder.HasIndex(d => new { d.OwnerId, d.ScheduledAt })
            .HasDatabaseName("ix_doses_due")
            .HasFilter("status = 1");

        builder.HasOne<Medication>()
            .WithMany()
            .HasForeignKey(d => d.MedicationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExerciseConfiguration : IEntityTypeConfiguration<Exercise>
{
    public void Configure(EntityTypeBuilder<Exercise> builder)
    {
        builder.ToTable("exercises", t => t.HasCheckConstraint(
            "ck_exercise_owner", "is_system = (owner_id IS NULL)"));

        builder.Property(e => e.Name).HasMaxLength(120).IsRequired();
        builder.Property(e => e.Category).HasConversion<short>();

        builder.HasIndex(e => e.OwnerId)
            .HasDatabaseName("ix_exercises_owner")
            .HasFilter("owner_id IS NOT NULL");

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(e => e.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class WorkoutPlanConfiguration : IEntityTypeConfiguration<WorkoutPlan>
{
    public void Configure(EntityTypeBuilder<WorkoutPlan> builder)
    {
        builder.ToTable("workout_plans");
        builder.Property(p => p.Name).HasMaxLength(120).IsRequired();

        // Owner-leading, like every owned table: the global filter puts
        // owner_id in front of every query, so an index that does not lead
        // with it cannot be used.
        builder.HasIndex(p => new { p.OwnerId, p.Name })
            .HasDatabaseName("ix_workout_plans_owner")
            .HasFilter("deleted_at IS NULL");
    }
}

public sealed class WorkoutPlanItemConfiguration : IEntityTypeConfiguration<WorkoutPlanItem>
{
    public void Configure(EntityTypeBuilder<WorkoutPlanItem> builder)
    {
        builder.ToTable("workout_plan_items", t => t.HasCheckConstraint(
            "ck_target", "target_reps IS NOT NULL OR target_seconds IS NOT NULL"));

        builder.HasOne<WorkoutPlan>()
            .WithMany()
            .HasForeignKey(i => i.PlanId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT: an exercise still used by a plan cannot be deleted out from
        // under it, which would leave an item pointing at nothing.
        builder.HasOne<Exercise>()
            .WithMany()
            .HasForeignKey(i => i.ExerciseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.PlanId);
    }
}

public sealed class WorkoutSessionConfiguration : IEntityTypeConfiguration<WorkoutSession>
{
    public void Configure(EntityTypeBuilder<WorkoutSession> builder)
    {
        builder.ToTable("workout_sessions");

        builder.Property(s => s.Status).HasConversion<short>();

        builder.HasIndex(s => new { s.OwnerId, s.ScheduledOn })
            .HasDatabaseName("ix_workout_owner")
            .IsDescending(false, true);

        builder.HasOne<WorkoutPlan>()
            .WithMany()
            .HasForeignKey(s => s.PlanId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class WorkoutSetConfiguration : IEntityTypeConfiguration<WorkoutSet>
{
    public void Configure(EntityTypeBuilder<WorkoutSet> builder)
    {
        builder.ToTable("workout_sets");

        builder.Property(s => s.WeightKg).HasPrecision(6, 2);

        builder.HasOne<WorkoutSession>()
            .WithMany()
            .HasForeignKey(s => s.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Exercise>()
            .WithMany()
            .HasForeignKey(s => s.ExerciseId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.SessionId);
    }
}

public sealed class FoodConfiguration : IEntityTypeConfiguration<Food>
{
    public void Configure(EntityTypeBuilder<Food> builder)
    {
        builder.ToTable("foods");

        builder.Property(f => f.Name).HasMaxLength(200).IsRequired();
        builder.Property(f => f.ServingDesc).HasMaxLength(60).IsRequired();
        builder.Property(f => f.ServingGrams).HasPrecision(8, 2);
        builder.Property(f => f.Kcal).HasPrecision(8, 2);
        builder.Property(f => f.ProteinG).HasPrecision(8, 2);
        builder.Property(f => f.CarbG).HasPrecision(8, 2);
        builder.Property(f => f.FatG).HasPrecision(8, 2);

        builder.HasIndex(f => f.OwnerId).HasFilter("owner_id IS NOT NULL");
        builder.HasIndex(f => f.Name);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(f => f.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MealConfiguration : IEntityTypeConfiguration<Meal>
{
    public void Configure(EntityTypeBuilder<Meal> builder)
    {
        builder.ToTable("meals");

        builder.Property(m => m.MealType).HasConversion<short>();

        // One breakfast per day. Adding a second food to breakfast extends the
        // meal rather than creating a rival one.
        builder.HasIndex(m => new { m.OwnerId, m.OnDate, m.MealType })
            .HasDatabaseName("uq_meal")
            .IsUnique();
    }
}

public sealed class MealItemConfiguration : IEntityTypeConfiguration<MealItem>
{
    public void Configure(EntityTypeBuilder<MealItem> builder)
    {
        builder.ToTable("meal_items");

        builder.Property(i => i.Quantity).HasPrecision(8, 2);
        builder.Property(i => i.Kcal).HasPrecision(8, 2);
        builder.Property(i => i.ProteinG).HasPrecision(8, 2);
        builder.Property(i => i.CarbG).HasPrecision(8, 2);
        builder.Property(i => i.FatG).HasPrecision(8, 2);

        builder.HasOne<Meal>()
            .WithMany()
            .HasForeignKey(i => i.MealId)
            .OnDelete(DeleteBehavior.Cascade);

        // RESTRICT rather than cascade: the snapshotted macros are the record,
        // and deleting a catalogue food must not erase what was eaten.
        builder.HasOne<Food>()
            .WithMany()
            .HasForeignKey(i => i.FoodId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.MealId);
    }
}

public sealed class HealthGoalConfiguration : IEntityTypeConfiguration<HealthGoal>
{
    public void Configure(EntityTypeBuilder<HealthGoal> builder)
    {
        builder.ToTable("health_goals");

        builder.HasKey(g => g.OwnerId);
        builder.Property(g => g.WeightTargetKg).HasPrecision(5, 2);

        builder.HasOne<AppUser>()
            .WithMany()
            .HasForeignKey(g => g.OwnerId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BodyMeasurementConfiguration : IEntityTypeConfiguration<BodyMeasurement>
{
    public void Configure(EntityTypeBuilder<BodyMeasurement> builder)
    {
        builder.ToTable("body_measurements");

        builder.Property(m => m.WeightKg).HasPrecision(5, 2);

        // One per day: weighing yourself twice corrects the entry rather than
        // creating a second truth for the same morning.
        builder.HasIndex(m => new { m.OwnerId, m.OnDate })
            .HasDatabaseName("uq_measure")
            .IsUnique();
    }
}
