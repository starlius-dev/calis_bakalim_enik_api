using CalisBakalimEnik.Api.Extensions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// What decides commit from rollback — docs/ARCHITECTURE.md §3.
/// </summary>
/// <remarks>
/// The filter runs BEFORE the result is executed, so the response status is
/// still the default 200 at that point. Reading it there would commit every
/// failure, which is the whole reason this decision is made from the result
/// object instead.
/// </remarks>
public class TransactionFilterTests
{
    [Fact]
    public void A_2xx_result_commits()
    {
        TransactionFilter.Succeeded(Results.Ok(new { id = 1 })).Should().BeTrue();
        TransactionFilter.Succeeded(Results.NoContent()).Should().BeTrue();
        TransactionFilter.Succeeded(Results.Created("/api/v1/tasks/1", new { }))
            .Should().BeTrue();
    }

    [Fact]
    public void A_validation_failure_rolls_back()
    {
        var result = Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["title"] = ["Başlık boş olamaz."],
        });

        // The case that matters: a handler that wrote something and THEN
        // refused the request must not leave the write behind.
        TransactionFilter.Succeeded(result).Should().BeFalse();
    }

    [Theory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public void Every_failure_status_rolls_back(int status)
    {
        TransactionFilter.Succeeded(Results.StatusCode(status)).Should().BeFalse();
    }

    [Fact]
    public void A_result_with_no_status_of_its_own_commits()
    {
        // Minimal APIs serialise a bare value as 200. Treating "no status" as a
        // failure would roll back every handler that returns its DTO directly.
        TransactionFilter.Succeeded(new { id = 1 }).Should().BeTrue();
        TransactionFilter.Succeeded(null).Should().BeTrue();
    }

    [Fact]
    public void The_redirect_range_is_not_a_failure()
    {
        // 3xx is not an error, and a handler that redirects after writing has
        // done its job.
        TransactionFilter.Succeeded(Results.StatusCode(StatusCodes.Status304NotModified))
            .Should().BeTrue();
    }
}
