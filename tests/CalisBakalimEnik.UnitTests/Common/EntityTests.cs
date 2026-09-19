using CalisBakalimEnik.Domain.Common;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Common;

file sealed class SampleEntity : OwnedEntity;

public class EntityTests
{
    [Fact]
    public void New_entities_get_a_uuid_v7()
    {
        var entity = new SampleEntity();

        entity.Id.Should().NotBe(Guid.Empty);
        entity.Id.Version.Should().Be(7, "UUIDv7 is time-ordered, so index locality is good");
    }

    [Fact]
    public void Ids_are_monotonically_ordered()
    {
        var first = new SampleEntity().Id;
        Thread.Sleep(2);
        var second = new SampleEntity().Id;

        // Compared as strings because Guid's own comparison is not byte order.
        first.ToString().CompareTo(second.ToString())
            .Should().BeLessThan(0, "later rows must sort after earlier ones");
    }
}
