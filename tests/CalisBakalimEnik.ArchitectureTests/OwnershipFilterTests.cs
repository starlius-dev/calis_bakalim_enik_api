using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Common;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.ArchitectureTests;

/// <summary>
/// The guard from docs/DATABASE.md §2, asserted against the REAL EF model.
///
/// These build the model without opening a connection, so they run in CI with no
/// database. That matters: the property being protected here — that a row
/// belonging to someone else is invisible — must be checked on every build, not
/// only when an integration environment happens to exist.
/// </summary>
public class OwnershipFilterTests : IDisposable
{
    private readonly AppDbContext _db;

    public OwnershipFilterTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=x;Password=y")
            .UseSnakeCaseNamingConvention()
            .Options;

        _db = new AppDbContext(options, new FakeCurrentUser());
    }

    [Fact]
    public void Every_owned_entity_has_a_global_query_filter()
    {
        var unprotected = _db.Model.GetEntityTypes()
            .Where(t => typeof(OwnedEntity).IsAssignableFrom(t.ClrType))
            .Where(t => t.GetQueryFilter() is null)
            .Select(t => t.ClrType.Name)
            .ToList();

        unprotected.Should().BeEmpty(
            "an OwnedEntity without a query filter leaks every user's rows to every other user");
    }

    [Fact]
    public void Every_owned_entity_has_an_owner_id_leading_index()
    {
        var offenders = new List<string>();

        foreach (var entity in _db.Model.GetEntityTypes()
                     .Where(t => typeof(OwnedEntity).IsAssignableFrom(t.ClrType)))
        {
            var hasLeadingOwner = entity.GetIndexes().Any(i =>
                i.Properties.Count > 0 &&
                i.Properties[0].Name == nameof(OwnedEntity.OwnerId));

            if (!hasLeadingOwner) offenders.Add(entity.ClrType.Name);
        }

        offenders.Should().BeEmpty(
            "a bare (due_at) index is useless here — (owner_id, due_at) is the one the planner picks");
    }

    [Fact]
    public void Identity_keeps_its_global_unique_index_on_email()
    {
        // Without a tenant, email is a GLOBAL identity. An earlier revision of the
        // design called for dropping this in favour of per-tenant partial indexes;
        // doing so would let two accounts share an address.
        var users = _db.Model.FindEntityType(typeof(Infrastructure.Identity.AppUser))!;

        var emailIndex = users.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(Infrastructure.Identity.AppUser.NormalizedEmail));

        emailIndex.Should().NotBeNull("Identity's EmailIndex must survive");
        emailIndex!.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void Device_fcm_token_is_globally_unique_not_per_user()
    {
        // FCM tokens are globally unique. A per-user index would let a reinstalled
        // phone keep delivering the previous owner's notifications to a new account.
        var devices = _db.Model.FindEntityType(typeof(Device))!;

        var index = devices.GetIndexes().FirstOrDefault(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(Device.FcmToken));

        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void Refresh_tokens_are_indexed_by_family_for_reuse_detection()
    {
        var tokens = _db.Model.FindEntityType(typeof(RefreshToken))!;

        tokens.GetIndexes()
            .Any(i => i.Properties.Any(p => p.Name == nameof(RefreshToken.FamilyId)))
            .Should().BeTrue("revoking a whole family on reuse must not table-scan");

        tokens.GetIndexes()
            .First(i => i.Properties.Count == 1
                        && i.Properties[0].Name == nameof(RefreshToken.TokenHash))
            .IsUnique.Should().BeTrue();
    }

    [Fact]
    public void Identity_join_tables_use_the_projects_naming_convention()
    {
        var names = _db.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(n => n is not null)
            .ToList();

        names.Should().NotContain(n => n!.StartsWith("AspNet"),
            "the snake_case convention renames columns but not Identity's hard-coded table names");
    }

    public void Dispose() => _db.Dispose();

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id => Guid.Empty;
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => false;
    }
}
