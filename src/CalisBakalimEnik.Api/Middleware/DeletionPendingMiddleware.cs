using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Models;
using CalisBakalimEnik.Infrastructure.Identity;

namespace CalisBakalimEnik.Api.Middleware;

/// <summary>
/// Locks an account whose deletion is pending (D15) down to the few things
/// that still make sense: seeing who you are, cancelling, downloading your
/// data first, and signing out.
/// </summary>
/// <remarks>
/// Read from the token's <c>del</c> claim, so it costs nothing per request.
/// Requesting deletion revokes every earlier token (the per-user cutoff), so
/// the only tokens a pending account can hold are ones that carry the claim;
/// cancelling and refreshing issues tokens without it.
/// </remarks>
public sealed class DeletionPendingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.HasClaim(TokenService.DeletionPendingClaim, "1")
            && !IsAllowed(context.Request.Method, context.Request.Path))
        {
            var result = MfaEndpoints.Problem(
                new Error("account.deletion_pending",
                    "Hesabın silinmek üzere. Devam etmek için silmeyi iptal et."),
                StatusCodes.Status409Conflict,
                context);

            await result.ExecuteAsync(context);
            return;
        }

        await next(context);
    }

    public static bool IsAllowed(string method, PathString path)
    {
        var p = path.Value?.TrimEnd('/').ToLowerInvariant() ?? string.Empty;

        return p.StartsWith("/api/v1/account", StringComparison.Ordinal)
               || p.StartsWith("/api/health", StringComparison.Ordinal)
               || p == "/api/version"
               || (p == "/api/v1/auth/me" && HttpMethods.IsGet(method))
               || p is "/api/v1/auth/logout" or "/api/v1/auth/logout-all" or "/api/v1/auth/refresh"
               // Switching or resending an MFA challenge during the step-up
               // for a last export.
               || p is "/api/v1/auth/mfa/select" or "/api/v1/auth/mfa/resend";
    }
}
