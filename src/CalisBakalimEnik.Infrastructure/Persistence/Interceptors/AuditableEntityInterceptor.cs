using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CalisBakalimEnik.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Stamps audit columns and OwnerId. OwnerId is set once on insert and its
/// modification is rejected — a row cannot change hands. See docs/DATABASE.md §2.
/// </summary>
public sealed class AuditableEntityInterceptor(IClock clock, ICurrentUser currentUser)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null) return;

        var now = clock.UtcNow;
        var userId = currentUser.Id;

        foreach (var entry in context.ChangeTracker.Entries<AuditableEntity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.CreatedBy = userId;
                    StampOwnerOnInsert(entry, userId);
                    break;

                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedBy = userId;
                    RejectOwnerChange(entry);
                    break;
            }
        }
    }

    private static void StampOwnerOnInsert(EntityEntry<AuditableEntity> entry, Guid? userId)
    {
        if (entry.Entity is not OwnedEntity owned) return;

        if (owned.OwnerId == Guid.Empty)
        {
            owned.OwnerId = userId
                ?? throw new InvalidOperationException(
                    $"Cannot insert {entry.Entity.GetType().Name}: no authenticated user to own it.");
        }
    }

    private static void RejectOwnerChange(EntityEntry<AuditableEntity> entry)
    {
        if (entry.Entity is not OwnedEntity) return;

        var owner = entry.Property(nameof(OwnedEntity.OwnerId));
        if (owner.IsModified)
        {
            throw new InvalidOperationException(
                $"OwnerId of {entry.Entity.GetType().Name} is immutable; a row cannot change hands.");
        }
    }
}
