using CalisBakalimEnik.Infrastructure.Services;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Common;

public class SystemClockTests
{
    [Fact]
    public void Now_is_truncated_to_what_postgres_can_store()
    {
        var clock = new SystemClock();

        // timestamptz keeps microseconds; DateTimeOffset counts 100ns ticks.
        // A sub-microsecond remainder means the value echoed in a response
        // differs from the one the database keeps.
        for (var i = 0; i < 50; i++)
        {
            (clock.UtcNow.Ticks % 10).Should().Be(0);
        }
    }

    [Fact]
    public void It_still_tells_the_time()
    {
        var clock = new SystemClock();

        clock.UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        clock.UtcNow.Offset.Should().Be(TimeSpan.Zero);
    }
}
