using System.Text.Json;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 9457 problem+json. A 500 never leaks a stack trace or an SQL
/// fragment to the client — the correlation id is the handle. See docs/ARCHITECTURE.md §6.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IHostEnvironment environment)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName] as string;

        var (status, title, type, detail) = exception switch
        {
            AppValidationException => (StatusCodes.Status400BadRequest, "Validation failed",
                ProblemTypes.Validation, "One or more fields are invalid."),
            UnauthorizedException e => (StatusCodes.Status401Unauthorized, "Unauthorized",
                ProblemTypes.Unauthorized, e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "Forbidden",
                ProblemTypes.Forbidden, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found",
                ProblemTypes.NotFound, e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, "Conflict",
                ProblemTypes.Conflict, e.Message),
            // EF's optimistic concurrency failure IS a conflict. Left to the
            // fallback it became a 500, which tells the client to give up on
            // something a retry would have fixed.
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Conflict",
                ProblemTypes.Conflict, "The resource changed while you were editing it."),
            // A body that could not be read at all: absent, empty, truncated,
            // not JSON, or the wrong shape entirely. The framework raises this
            // while binding, BEFORE any handler runs, so no endpoint's own
            // validation ever gets the chance to answer - and left to the
            // fallback below it became a 500 on every write route, told the
            // caller the server was broken when the request was, and logged
            // each one as an unhandled error.
            //
            // The exception carries its own status for the cases that are not
            // 400 (a body over the size limit is a 413), so that is honoured
            // rather than flattened.
            BadHttpRequestException bad => (
                bad.StatusCode is >= 400 and < 500
                    ? bad.StatusCode
                    : StatusCodes.Status400BadRequest,
                "Invalid request", ProblemTypes.Validation,
                "İstek okunamadı. Gövdenin geçerli JSON olduğundan emin ol."),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest,
                "Client closed request", ProblemTypes.Cancelled, "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
                ProblemTypes.Internal, "An unexpected error occurred.")
        };

        if (status >= 500)
        {
            logger.LogError(exception,
                "Unhandled exception. CorrelationId={CorrelationId} Path={Path}",
                correlationId, context.Request.Path);
        }
        else
        {
            logger.LogWarning(
                "Request failed with {Status}. CorrelationId={CorrelationId} Path={Path} Reason={Reason}",
                status, correlationId, context.Request.Path, exception.GetType().Name);
        }

        if (context.Response.HasStarted)
        {
            logger.LogWarning("Response already started; cannot write a problem document.");
            return;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Type = type,
            Detail = detail,
            Instance = context.Request.Path
        };

        problem.Extensions["correlationId"] = correlationId;

        if (exception is AppValidationException validation)
            problem.Extensions["errors"] = validation.Errors;

        // Never in production: only a developer environment sees the real message.
        if (status >= 500 && environment.IsDevelopment())
            problem.Extensions["exception"] = exception.ToString();

        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(problem,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
