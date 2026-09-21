using System.Linq.Expressions;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Common;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Domain.Notifications;
using CalisBakalimEnik.Domain.Content;
using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Infrastructure.Persistence;

public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    ICurrentUser currentUser)
    : IdentityDbContext<AppUser, AppRole, Guid>(options), IAppDbContext
{
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<MfaFactor> MfaFactors => Set<MfaFactor>();
    public DbSet<MfaRecoveryCode> MfaRecoveryCodes => Set<MfaRecoveryCode>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    // Phase 5. These carry a UserId rather than an OwnerId on purpose: the
    // outbox processor has no request to resolve a current user from, and the
    // ownership filter would hide every row from it. See Notification's remarks.
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationDelivery> NotificationDeliveries =>
        Set<NotificationDelivery>();
    public DbSet<NotificationPreference> NotificationPreferences =>
        Set<NotificationPreference>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    // Phase 6. An OwnedEntity: the global ownership filter applies by
    // convention, so no query in the task endpoints filters by owner by hand.
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<ScheduleEntry> ScheduleEntries => Set<ScheduleEntry>();
    public DbSet<FocusSession> FocusSessions => Set<FocusSession>();

    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Note> Notes => Set<Note>();

    // Health (Phase 6). Exercises and foods are NOT owned entities: a system
    // row has no owner, and the ownership filter would hide the catalogue.
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<MedicationTime> MedicationTimes => Set<MedicationTime>();
    public DbSet<MedicationDose> MedicationDoses => Set<MedicationDose>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<WorkoutPlan> WorkoutPlans => Set<WorkoutPlan>();
    public DbSet<WorkoutPlanItem> WorkoutPlanItems => Set<WorkoutPlanItem>();
    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();
    public DbSet<WorkoutSet> WorkoutSets => Set<WorkoutSet>();
    public DbSet<Food> Foods => Set<Food>();
    public DbSet<Meal> Meals => Set<Meal>();
    public DbSet<MealItem> MealItems => Set<MealItem>();
    public DbSet<HealthGoal> HealthGoals => Set<HealthGoal>();
    public DbSet<BodyMeasurement> BodyMeasurements => Set<BodyMeasurement>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        RenameIdentityTables(builder);
        ApplyOwnershipFilters(builder);
        ApplySoftDeleteFilters(builder);
    }

    /// <summary>
    /// Identity's join tables keep their default AspNet* names unless told
    /// otherwise — the snake_case convention only renames columns, not the
    /// hard-coded table names. docs/DATABASE.md §3 specifies these.
    /// </summary>
    private static void RenameIdentityTables(ModelBuilder builder)
    {
        builder.Entity<IdentityUserRole<Guid>>().ToTable("user_roles");
        builder.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims");
        builder.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims");
        builder.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins");
        builder.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens");
    }

    /// <summary>
    /// Applies the global query filter on <c>OwnerId</c> to every
    /// <see cref="OwnedEntity"/>, so a row belonging to someone else is simply
    /// invisible rather than merely un-returned by a handler that remembers to
    /// filter. See docs/DATABASE.md §2.
    ///
    /// Applied by convention rather than per-entity: a new table added in Phase 6
    /// is protected the moment it derives from OwnedEntity, and cannot silently
    /// opt out by someone forgetting a line in its configuration.
    /// </summary>
    private void ApplyOwnershipFilters(ModelBuilder builder)
    {
        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (!typeof(OwnedEntity).IsAssignableFrom(entityType.ClrType)) continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");

            // e.OwnerId == currentUser.Id
            var ownerId = Expression.Property(parameter, nameof(OwnedEntity.OwnerId));
            var current = Expression.Property(
                Expression.Constant(this), nameof(CurrentUserId));
            var ownerMatches = Expression.Equal(
                ownerId, Expression.Convert(current, typeof(Guid)));

            // && e.DeletedAt == null
            var deletedAt = Expression.Property(parameter, nameof(AuditableEntity.DeletedAt));
            var notDeleted = Expression.Equal(
                deletedAt, Expression.Constant(null, typeof(DateTimeOffset?)));

            builder.Entity(entityType.ClrType).HasQueryFilter(
                Expression.Lambda(Expression.AndAlso(ownerMatches, notDeleted), parameter));
        }
    }

    /// <summary>
    /// Tables that belong to a user but are not domain rows (devices, refresh
    /// tokens) are soft-delete filtered only — they are reached through the
    /// authenticated user, not through OwnerId.
    /// </summary>
    private static void ApplySoftDeleteFilters(ModelBuilder builder)
    {
        builder.Entity<Device>().HasQueryFilter(d => d.DeletedAt == null);
    }

    /// <summary>
    /// Read by the compiled query filter. Guid.Empty when unauthenticated, which
    /// matches no row — an anonymous request sees nothing rather than everything.
    /// </summary>
    public Guid CurrentUserId => currentUser.Id ?? Guid.Empty;
}
