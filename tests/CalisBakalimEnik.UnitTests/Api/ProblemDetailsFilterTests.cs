using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Middleware;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// The problem documents endpoints return by hand — docs/ARCHITECTURE.md §3.
/// </summary>
public class ProblemDetailsFilterTests
{
    private const string CorrelationId = "0193f2b1-dead-beef-cafe-000000000001";

    private static HttpContext Request()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/tasks";
        http.Items[CorrelationIdMiddleware.HeaderName] = CorrelationId;
        return http;
    }

    /// <summary>
    /// The document inside a result.
    /// </summary>
    /// <remarks>
    /// <c>Results.ValidationProblem</c> does NOT return the
    /// <c>ValidationProblem</c> type — it returns a <c>ProblemHttpResult</c>
    /// carrying an <c>HttpValidationProblemDetails</c>. Only
    /// <c>TypedResults.ValidationProblem</c> returns the former. The filter
    /// handles both, and this helper exists so these tests do not quietly
    /// assert against the branch the codebase never takes.
    /// </remarks>
    private static ProblemDetails Details(object? result) => result switch
    {
        ProblemHttpResult p => p.ProblemDetails,
        ValidationProblem v => v.ProblemDetails,
        _ => throw new InvalidOperationException($"not a problem result: {result?.GetType()}"),
    };

    private static async Task<object?> RunAsync(HttpContext http, object? result)
    {
        var filter = new ProblemDetailsFilter();
        var context = EndpointFilterInvocationContext.Create(http);

        return await filter.InvokeAsync(context, _ => ValueTask.FromResult(result));
    }

    [Fact]
    public async Task A_returned_validation_problem_gets_the_correlation_id()
    {
        var result = Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["title"] = ["Başlık boş olamaz."],
        });

        await RunAsync(Request(), result);

        // The handle the user quotes from a screenshot. A validation failure is
        // exactly the response someone is looking at when they ask why they
        // were refused, and it was the one document going out without one.
        var problem = Details(result);
        problem.Extensions["correlationId"].Should().Be(CorrelationId);
    }

    [Fact]
    public async Task The_field_errors_survive()
    {
        var result = Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["title"] = ["Başlık boş olamaz."],
        });

        await RunAsync(Request(), result);

        // Amended in place rather than rebuilt: a rebuilt document would drop
        // the very thing a validation failure exists to carry.
        var problem = Details(result).Should()
            .BeOfType<HttpValidationProblemDetails>().Subject;

        problem.Errors.Should().ContainKey("title");
        problem.Errors["title"].Should().Contain("Başlık boş olamaz.");
    }

    [Fact]
    public async Task It_fills_the_type_and_instance_when_they_are_missing()
    {
        var result = Results.ValidationProblem(new Dictionary<string, string[]>());

        await RunAsync(Request(), result);

        var problem = Details(result);
        problem.Type.Should().Be(ProblemTypes.Validation);
        problem.Instance.Should().Be("/api/v1/tasks");
    }

    [Fact]
    public async Task A_type_the_endpoint_chose_is_left_alone()
    {
        var result = Results.Problem(
            title: "Çok fazla istek",
            statusCode: StatusCodes.Status429TooManyRequests,
            type: "https://example.test/mine");

        await RunAsync(Request(), result);

        // Setting it meant it. Overwriting would make the filter a rule that
        // cannot be opted out of.
        var problem = Details(result);
        problem.Type.Should().Be("https://example.test/mine");
    }

    [Fact]
    public async Task A_problem_result_gets_the_same_treatment_as_a_validation_one()
    {
        var result = Results.Problem(
            title: "Çakışma", statusCode: StatusCodes.Status409Conflict);

        await RunAsync(Request(), result);

        var problem = Details(result);
        problem.Type.Should().Be(ProblemTypes.Conflict);
        problem.Extensions["correlationId"].Should().Be(CorrelationId);
    }

    [Fact]
    public async Task A_successful_result_passes_through_untouched()
    {
        var result = Results.Ok(new { id = 1 });

        var returned = await RunAsync(Request(), result);

        returned.Should().BeSameAs(result);
    }

    [Fact]
    public async Task A_missing_correlation_id_is_not_an_error()
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/api/v1/tasks";

        var result = Results.Problem(statusCode: StatusCodes.Status404NotFound);

        // Nothing here may throw on a request that somehow arrived without the
        // correlation middleware: an error document that fails to render is a
        // 500 replacing a 404.
        var act = async () => await RunAsync(http, result);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void The_vocabulary_covers_every_status_the_API_answers_with()
    {
        // One table, three writers — the exception middleware, the rate-limit
        // middleware and this filter. The type URI is the part a client is
        // meant to branch on, so a status that mapped differently in two of
        // them would be worse than no type at all.
        ProblemTypes.ForStatus(StatusCodes.Status400BadRequest)
            .Should().Be(ProblemTypes.Validation);
        ProblemTypes.ForStatus(StatusCodes.Status401Unauthorized)
            .Should().Be(ProblemTypes.Unauthorized);
        ProblemTypes.ForStatus(StatusCodes.Status403Forbidden)
            .Should().Be(ProblemTypes.Forbidden);
        ProblemTypes.ForStatus(StatusCodes.Status404NotFound)
            .Should().Be(ProblemTypes.NotFound);
        ProblemTypes.ForStatus(StatusCodes.Status409Conflict)
            .Should().Be(ProblemTypes.Conflict);
        ProblemTypes.ForStatus(StatusCodes.Status429TooManyRequests)
            .Should().Be(ProblemTypes.RateLimited);
        ProblemTypes.ForStatus(StatusCodes.Status500InternalServerError)
            .Should().Be(ProblemTypes.Internal);
    }
}
