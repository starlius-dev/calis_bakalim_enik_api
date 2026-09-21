using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Common;
using CalisBakalimEnik.Domain.Plan;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.UnitTests.Plan;

/// <summary>
/// Guards the property the whole domain rests on: an owned row is invisible to
/// everyone else, by convention rather than by remembering.
/// </summary>
/// <remarks>
/// The model is built without ever opening a connection, so this runs anywhere.
/// </remarks>
public class TaskOwnershipTests
{
    private sealed class NoUser : ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => false;
    }

    private static AppDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new NoUser());
    }

    [Fact]
    public void Every_owned_entity_carries_an_ownership_filter()
    {
        using var db = Context();

        var owned = db.Model.GetEntityTypes()
            .Where(e => typeof(OwnedEntity).IsAssignableFrom(e.ClrType))
            .ToList();

        owned.Should().NotBeEmpty("the domain has at least one owned table by now");

        foreach (var entity in owned)
        {
            // The filter is applied by convention in AppDbContext, so a table
            // added in a later slice of Phase 6 is protected the moment it
            // derives from OwnedEntity — and this fails if someone opts one out.
            entity.GetQueryFilter()
                .Should().NotBeNull($"{entity.ClrType.Name} is owned and must be filtered");
        }
    }

    [Fact]
    public void Tasks_are_owned()
    {
        // Stated as a test rather than trusted to the base class: a task that
        // silently stopped being an OwnedEntity would still compile, still
        // serve, and leak every user's list to every other user.
        typeof(TaskItem).Should().BeAssignableTo<OwnedEntity>();

        using var db = Context();
        db.Model.FindEntityType(typeof(TaskItem))!.GetQueryFilter().Should().NotBeNull();
    }

    [Fact]
    public void An_unauthenticated_context_matches_no_rows_rather_than_all_of_them()
    {
        using var db = Context();

        // Guid.Empty owns nothing, so the filter yields an empty set. The
        // failure mode this rules out is the opposite one: a null current user
        // turning the filter into a no-op and exposing everything.
        db.CurrentUserId.Should().Be(Guid.Empty);
    }

    [Fact]
    public void The_three_priorities_are_the_three_the_design_draws()
    {
        // Düşük / Orta / Acil. A fourth value would be a row the UI cannot
        // render. See docs/DATABASE.md §8.1.
        Enum.GetValues<TaskPriority>().Should().HaveCount(3);
        Enum.GetValues<TaskState>().Should().HaveCount(3);
    }
}
