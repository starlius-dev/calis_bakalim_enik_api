using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Persistence;

/// <summary>
/// The starter exercise catalogue the seeder inserts.
/// </summary>
/// <remarks>
/// The seeder matches entries by name, case-insensitively. Two lines that
/// differ only in case would both be inserted on a fresh database and then
/// look like a duplicate in the picker forever, so that is checked here rather
/// than noticed later.
/// </remarks>
public class ExerciseCatalogueTests
{
    [Fact]
    public void Names_are_unique_ignoring_case()
    {
        ExerciseCatalogue.Entries
            .Select(e => e.Name.ToLowerInvariant())
            .Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_entry_is_named_and_tagged()
    {
        ExerciseCatalogue.Entries.Should().AllSatisfy(e =>
        {
            e.Name.Should().NotBeNullOrWhiteSpace();
            e.Name.Should().Be(e.Name.Trim());
            e.Muscles.Should().NotBeEmpty();
            Enum.IsDefined(e.Category).Should().BeTrue();
        });
    }

    [Fact]
    public void Every_category_has_something_to_pick()
    {
        foreach (var category in Enum.GetValues<ExerciseCategory>())
            ExerciseCatalogue.Entries.Should().Contain(e => e.Category == category);
    }
}
