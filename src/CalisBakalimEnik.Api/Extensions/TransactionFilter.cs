using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Extensions;

/// <summary>
/// Makes one handler's writes atomic.
/// </summary>
/// <remarks>
/// Three handlers save twice in a single request, because the second write
/// needs an id the first one generated: creating a task or an event then
/// scheduling its reminder, and disabling an account then recording why. Each
/// has a window where the first save has committed and the second has not — a
/// task whose reminder will never fire, or, worst of the three, <b>an account
/// disabled with nothing in the audit trail saying who did it</b>.
///
/// <para><b>This is opt-in, and must stay opt-in.</b> Applying it to everything
/// would break authentication. The login path deliberately writes on its
/// failure path — <c>BruteForceGuard</c> records the failed attempt and the
/// security event, then answers 401 — and a filter that rolls back whenever the
/// status is not a success would erase exactly those rows. Lockout would still
/// appear to work in every test that checks one response, while the counter it
/// depends on silently reset on each attempt. A blanket transaction filter is
/// how brute-force protection gets turned off by accident.</para>
///
/// So: <c>.Transactional()</c> goes on handlers whose writes must land together,
/// and nowhere near <c>/auth</c>.
/// </remarks>
public sealed class TransactionFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // RequestServices, not ApplicationServices: the DbContext is scoped,
        // and the filter instance outlives the request.
        var db = http.RequestServices.GetRequiredService<AppDbContext>();

        // Npgsql has no nested transactions. If something upstream already
        // opened one, join it — starting a second would throw.
        if (db.Database.CurrentTransaction is not null) return await next(context);

        // The context is registered with EnableRetryOnFailure(3), and a
        // retrying execution strategy REFUSES a transaction opened by hand:
        // it cannot retry an operation whose transaction it does not own.
        // Calling BeginTransactionAsync directly throws InvalidOperationException
        // on every request to this endpoint — so the whole unit, handler
        // included, is handed to the strategy instead.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry re-runs the handler, and the entities the failed attempt
            // added are still tracked as Added — the rollback undid the
            // database, not the change tracker. Without this, a transient
            // failure on the first attempt would insert the task TWICE on the
            // second. This is also why the filter belongs only on handlers
            // that start from nothing: it discards whatever was tracked before.
            db.ChangeTracker.Clear();

            // Not http.RequestAborted: a cancelled request must still be able
            // to roll back, and a cancelled token makes the rollback throw on
            // top of whatever went wrong.
            await using var transaction = await db.Database.BeginTransactionAsync(
                CancellationToken.None);

            var result = await next(context);

            // An exception skips this and leaves the transaction uncommitted;
            // disposing it rolls back. That is the path a 500 takes.
            if (Succeeded(result)) await transaction.CommitAsync(CancellationToken.None);
            else await transaction.RollbackAsync(CancellationToken.None);

            return result;
        });
    }

    /// <summary>
    /// Reads the status from the RESULT, not from the response.
    /// </summary>
    /// <remarks>
    /// The filter runs before the result is executed, so
    /// <c>Response.StatusCode</c> is still the default 200 at this point and
    /// would commit every failure.
    ///
    /// A result that carries no status is a value the framework will serialise
    /// as 200, so it counts as success.
    /// </remarks>
    public static bool Succeeded(object? result) =>
        result is not IStatusCodeHttpResult { StatusCode: >= 400 };
}

public static class TransactionFilterExtensions
{
    /// <summary>
    /// Wraps this endpoint's writes in one transaction. See the remarks on
    /// <see cref="TransactionFilter"/> before adding it to an auth route.
    /// </summary>
    public static TBuilder Transactional<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilterFactory((context, next) =>
        {
            var filter = new TransactionFilter();
            return invocation => filter.InvokeAsync(invocation, next);
        });

        return builder;
    }
}
