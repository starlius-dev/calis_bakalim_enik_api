namespace CalisBakalimEnik.Domain.Common;

/// <summary>Root of every persisted entity. Ids are UUIDv7 — see docs/DATABASE.md §1.</summary>
public abstract class BaseEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
}
