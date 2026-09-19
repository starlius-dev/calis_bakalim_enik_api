using System.Text.Json;
using CalisBakalimEnik.Application.Common.Exceptions;
using CalisBakalimEnik.Api.Middleware;
using Microsoft.AspNetCore.Mvc;

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
    private const string BaseType = "https://calisbakalimenik.app/errors/";

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
                "validation", "One or more fields are invalid."),
            UnauthorizedException e => (StatusCodes.Status401Unauthorized, "Unauthorized",
                "unauthorized", e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "Forbidden",
                "forbidden", e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, "Not found",
                "not-found", e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, "Conflict",
                "conflict", e.Message),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest,
                "Client closed request", "cancelled", "The request was cancelled."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
                "internal", "An unexpected error occurred.")
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
            Type = BaseType + type,
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
