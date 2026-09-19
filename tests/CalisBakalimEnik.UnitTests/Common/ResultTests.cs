using CalisBakalimEnik.Application.Common.Models;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Common;

public class ResultTests
{
    [Fact]
    public void Success_carries_its_value()
    {
        var result = Result.Success(42);

        result.Succeeded.Should().BeTrue();
        result.Failed.Should().BeFalse();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_carries_its_error()
    {
        var error = new Error("courses.not_found", "Ders bulunamadı.");

        var result = Result.Failure<int>(error);

        result.Failed.Should().BeTrue();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Reading_the_value_of_a_failure_throws()
    {
        var result = Result.Failure<int>(new Error("x", "y"));

        var act = () => result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_success_cannot_carry_an_error()
    {
        var act = () => Result.Failure(Error.None);

        act.Should().Throw<InvalidOperationException>();
    }
}
