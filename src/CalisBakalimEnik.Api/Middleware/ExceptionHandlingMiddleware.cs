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

    /// <summary>
    /// Whether the caller sent a request body at all, so a binding failure can
    /// point at the right half of the request.
    /// </summary>
    private static bool HasBody(HttpContext context) =>
        context.Request.ContentLength > 0
        || context.Request.Headers.ContainsKey("Transfer-Encoding");

    private async Task WriteProblemAsync(HttpContext context, Exception exception)
    {
        var correlationId = context.Items[CorrelationIdMiddleware.HeaderName] as string;

        var (status, title, type, detail) = exception switch
        {
            // Turkish, like every other message a user can end up reading.
            // These come from the middleware rather than from an endpoint, so
            // ProblemDetailsFilter never sees them and they stayed English long
            // after the endpoint-returned ones were translated.
            AppValidationException => (StatusCodes.Status400BadRequest, "Geçersiz istek",
                ProblemTypes.Validation, "Bir ya da daha fazla alan geçersiz."),
            UnauthorizedException e => (StatusCodes.Status401Unauthorized, "Yetkisiz",
                ProblemTypes.Unauthorized, e.Message),
            ForbiddenException e => (StatusCodes.Status403Forbidden, "İzin yok",
                ProblemTypes.Forbidden, e.Message),
            NotFoundException e => (StatusCodes.Status404NotFound, "Bulunamadı",
                ProblemTypes.NotFound, e.Message),
            ConflictException e => (StatusCodes.Status409Conflict, "Çakışma",
                ProblemTypes.Conflict, e.Message),
            // EF's optimistic concurrency failure IS a conflict. Left to the
            // fallback it became a 500, which tells the client to give up on
            // something a retry would have fixed.
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Çakışma",
                ProblemTypes.Conflict, "Kayıt sen düzenlerken değişti. Tekrar dene."),
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
                "Geçersiz istek", ProblemTypes.Validation,
                // The same exception covers an unreadable BODY and an
                // unbindable QUERY value, and telling someone to check their
                // JSON when they sent a GET with no body at all sends them
                // looking in the wrong place. A cursor with an unencoded "+" in
                // it does exactly that.
                HasBody(context)
                    ? "İstek gövdesi okunamadı. Geçerli JSON gönder."
                    : "İstek okunamadı. Adresteki değerleri kontrol et."),
            OperationCanceledException => (StatusCodes.Status499ClientClosedRequest,
                "İstek iptal edildi", ProblemTypes.Cancelled, "İstek iptal edildi."),
            _ => (StatusCodes.Status500InternalServerError, "Beklenmeyen hata",
                ProblemTypes.Internal, "Beklenmeyen bir hata oluştu.")
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
