using System.Reflection;
using CalisBakalimEnik.Domain.Common;
using FluentAssertions;

namespace CalisBakalimEnik.ArchitectureTests;

/// <summary>
/// The guard from docs/DATABASE.md §2. Today it asserts the shape of the base types;
/// once entities land in Phase 3 it grows to enumerate every OwnedEntity and fail if
/// one lacks a query filter or an owner_id-leading index. New tables cannot silently
/// opt out.
/// </summary>
public class OwnershipConventionTests
{
    private static readonly Assembly Domain = typeof(BaseEntity).Assembly;

    [Fact]
    public void Every_owned_entity_is_auditable()
    {
        // Passes vacuously until entities land in Phase 3 — AllSatisfy would throw on an
        // empty set, which would be a false alarm rather than a real finding.
        var offenders = Domain.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(OwnedEntity).IsAssignableFrom(t))
            .Where(t => !typeof(AuditableEntity).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("every type that owns user data must carry audit columns");
    }

    [Fact]
    public void Owned_entity_exposes_an_owner_id()
    {
        typeof(OwnedEntity).GetProperty(nameof(OwnedEntity.OwnerId))
            .Should().NotBeNull("the global query filter depends on this property name");
    }

    [Fact]
    public void Auditable_entity_exposes_a_soft_delete_column()
    {
        typeof(AuditableEntity).GetProperty(nameof(AuditableEntity.DeletedAt))
            .Should().NotBeNull("the global query filter also excludes soft-deleted rows");
    }
}
