namespace CalisBakalimEnik.Domain.Common;

/// <summary>
/// Every row a user privately owns. Filtered by a global query filter on
/// <see cref="OwnerId"/>; stamped on insert and immutable thereafter.
/// See docs/DATABASE.md §2.
/// </summary>
public abstract class OwnedEntity : AuditableEntity
{
    public Guid OwnerId { get; set; }
}
