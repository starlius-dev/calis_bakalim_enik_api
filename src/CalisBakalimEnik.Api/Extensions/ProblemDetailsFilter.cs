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

        if (problem is not null)
        {
            Complete(problem, context.HttpContext);
            return result;
        }

        // A failure with NO BODY AT ALL — Results.NotFound(), .Unauthorized(),
        // .Forbid() and friends, of which there are around forty across the
        // feature folders.
        //
        // They were going out as a bare status line: no type, no title, no
        // correlationId, nothing for a client to show or a user to quote. The
        // SAME failure raised as a NotFoundException came back as a full
        // problem document, so the API answered "this does not exist" in two
        // different shapes depending on which line of code noticed. An unknown
        // id is the most common failure a client will ever see, and it was the
        // one carrying the least.
        //
        // Results with a value are left alone: Results.NotFound(someObject) is
        // an endpoint deliberately saying something.
        if (result is IStatusCodeHttpResult { StatusCode: >= 400 } bare
            && result is not IValueHttpResult { Value: not null })
        {
            return Bodyless(bare.StatusCode.Value, context.HttpContext);
        }

        return result;
    }

    /// <summary>Turns a bare failure status into this API's problem document.</summary>
    private static IResult Bodyless(int status, HttpContext http)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = TitleFor(status),
            Type = ProblemTypes.ForStatus(status),
            Instance = http.Request.Path,
        };

        problem.Extensions["correlationId"] =
            http.Items[CorrelationIdMiddleware.HeaderName] as string;

        return Results.Problem(problem);
    }

    private static string TitleFor(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Geçersiz istek",
        StatusCodes.Status401Unauthorized => "Yetkisiz",
        StatusCodes.Status403Forbidden => "İzin yok",
        StatusCodes.Status404NotFound => "Bulunamadı",
        StatusCodes.Status409Conflict => "Çakışma",
        StatusCodes.Status429TooManyRequests => "Çok fazla istek",
        _ => "İstek reddedildi",
    };

    private static void Complete(ProblemDetails problem, HttpContext http)
    {
        var status = problem.Status ?? http.Response.StatusCode;

        if (IsUnchosen(problem.Type)) problem.Type = ProblemTypes.ForStatus(status);

        // Same rule as Type, for the same reason: "did anyone choose it".
        // Results.ValidationProblem fills the title in with an English sentence
        // from the framework, so the one line a user is most likely to see on a
        // refused form was the one line not in their language.
        if (IsUnchosenTitle(problem.Title)) problem.Title = "Geçersiz istek";

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

    /// <summary>
    /// Whether this <c>title</c> is the framework's, rather than one an
    /// endpoint wrote. Anything else is a decision and is left alone.
    /// </summary>
    private static bool IsUnchosenTitle(string? title) =>
        string.IsNullOrEmpty(title) ||
        title == "One or more validation errors occurred.";
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
