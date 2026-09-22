using CalisBakalimEnik.Api.Middleware;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// Finishes the problem documents that endpoints build by hand.
/// </summary>
/// <remarks>
/// <see cref="ExceptionHandlingMiddleware"/> only sees failures that were
/// THROWN. Most of this API's 4xx responses are returned instead — around forty
/// <c>Results.ValidationProblem</c> calls, each one next to the rule it
/// enforces — and those never reach the middleware. They were going out with no
/// <c>correlationId</c> and with ASP.NET's default <c>type</c>, so the two
/// halves of the same API answered in two different shapes.
///
/// That gap had a cost: <b>the correlation id is the handle a user quotes from
/// a screenshot</b>, and a validation failure is exactly the kind of response a
/// user is looking at when they ask why something was refused. The one document
/// most likely to be reported was the one with nothing to report.
///
/// A filter rather than middleware, because the result has to be caught before
/// it is executed — once the response is written there is nothing left to
/// amend. It is applied to the whole API in one place in <c>Program.cs</c>, for
/// the same reason the authenticated rate limiter is middleware: a convention
/// that must be remembered per route is one that will be forgotten on the route
/// that matters.
/// </remarks>
public sealed class ProblemDetailsFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);

        // Both carry a MUTABLE ProblemDetails, so the document is amended in
        // place rather than rebuilt — rebuilding would drop whatever the
        // endpoint put in `errors`.
        var problem = result switch
        {
            ProblemHttpResult p => p.ProblemDetails,
            ValidationProblem v => v.ProblemDetails,
            _ => null,
        };

        if (problem is not null) Complete(problem, context.HttpContext);

        return result;
    }

    private static void Complete(ProblemDetails problem, HttpContext http)
    {
        var status = problem.Status ?? http.Response.StatusCode;

        if (IsUnchosen(problem.Type)) problem.Type = ProblemTypes.ForStatus(status);

        problem.Instance ??= http.Request.Path;

        if (!problem.Extensions.ContainsKey("correlationId"))
        {
            problem.Extensions["correlationId"] =
                http.Items[CorrelationIdMiddleware.HeaderName] as string;
        }
    }

    /// <summary>
    /// Whether this <c>type</c> is one nobody picked.
    /// </summary>
    /// <remarks>
    /// A null check is not enough, and assuming otherwise is why the first
    /// version of this filter did nothing. <c>Results.Problem</c> and
    /// <c>Results.ValidationProblem</c> fill <c>Type</c> in at construction
    /// with the RFC link for the status — so the field is never null by the
    /// time a filter sees it, and every hand-returned failure was going out
    /// with <c>rfc9110#section-15.5.1</c> while thrown ones carried this API's
    /// own vocabulary. Same API, same kind of failure, two answers.
    ///
    /// So the rule is not "is it set" but "did anyone choose it". The
    /// framework's defaults all point at the RFC; anything else is a decision
    /// and is left alone.
    /// </remarks>
    private static bool IsUnchosen(string? type) =>
        string.IsNullOrEmpty(type) ||
        type.StartsWith("https://tools.ietf.org/html/rfc", StringComparison.Ordinal);
}

public static class ProblemDetailsFilterExtensions
{
    /// <summary>
    /// Completes every problem document the endpoints under
    /// <paramref name="builder"/> return.
    /// </summary>
    public static TBuilder CompleteProblemDetails<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((context, next) =>
        {
            var filter = new ProblemDetailsFilter();
            return invocation => filter.InvokeAsync(invocation, next);
        });

        return builder;
    }
}
