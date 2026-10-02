using CalisBakalimEnik.Api.Features.Health;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Which days GET /doses covers (J68, J69).
/// </summary>
/// <remarks>
/// The default used to be yesterday through tomorrow. The "today" list drew all
/// three days as one, and a tap on tomorrow's row was refused as a day early,
/// which looked like the app getting the time wrong.
/// </remarks>
public class DoseRangeTests
{
    private static readonly DateOnly Today = new(2026, 9, 30);

    [Fact]
    public void With_no_dates_it_is_today_and_only_today()
    {
        MedicationEndpoints.DoseRange(Today, null, null).Should().Be((Today, Today));
    }

    [Fact]
    public void An_explicit_range_is_used_as_given()
    {
        var from = Today.AddDays(-28);

        MedicationEndpoints.DoseRange(Today, from, Today).Should().Be((from, Today));
    }

    [Fact]
    public void One_end_given_leaves_the_other_at_today()
    {
        var tomorrow = Today.AddDays(1);

        MedicationEndpoints.DoseRange(Today, null, tomorrow).Should().Be((Today, tomorrow));
        MedicationEndpoints.DoseRange(Today, Today.AddDays(-1), null)
            .Should().Be((Today.AddDays(-1), Today));
    }
}
