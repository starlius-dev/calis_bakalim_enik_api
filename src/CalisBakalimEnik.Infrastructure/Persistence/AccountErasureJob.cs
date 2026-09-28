using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// How long a deletion request waits before the account is erased.
/// </summary>
public sealed class AccountDeletionOptions
{
    public const string SectionName = "AccountDeletion";

    /// <summary>
    /// Seven days, decided 29 Sep 2026: long enough to undo a mistake or a
    /// stolen session, short enough to honour "delete my data" promptly.
    /// </summary>
    public int GraceDays { get; set; } = 7;
}

/// <summary>
/// Erases accounts whose <see cref="AppUser.DeletionScheduledAt"/> has passed,
/// then tells the owner it is done.
/// </summary>
/// <remarks>
/// Hourly, so erasure lands within an hour of the promised time. The box
/// loses power now and then; a missed hour is simply caught up on the next
/// one, because the query is "due by now", not "due this hour".
/// </remarks>
public sealed class AccountErasureJob(
    IServiceScopeFactory scopes,
    IClock clock,
    ILogger<AccountErasureJob> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A short wait so a restart does not race the rest of startup.
        try { await Task.Delay(TimeSpan.FromMinutes(1), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Account erasure run failed.");
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var now = clock.UtcNow;
        var due = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.DeletionScheduledAt != null && u.DeletionScheduledAt <= now)
            .Select(u => new { u.Id, u.Email, u.DisplayName })
            .ToListAsync(ct);

        foreach (var user in due)
        {
            // One scope per account, so one failure cannot poison the others'
            // change tracker or transaction.
            using var accountScope = scopes.CreateScope();
            var erasure = accountScope.ServiceProvider.GetRequiredService<AccountErasure>();
            var email = accountScope.ServiceProvider.GetRequiredService<IEmailSender>();

            try
            {
                await erasure.EraseAsync(user.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not erase account {UserId}; will retry next run.", user.Id);
                continue;
            }

            if (string.IsNullOrWhiteSpace(user.Email)) continue;

            try
            {
                await email.SendAsync(user.Email, "Hesabın silindi", AccountEmails.Erased(user.DisplayName), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The erasure stands either way; the address is gone now, so
                // there is nothing to retry with.
                logger.LogWarning(ex, "Erased {UserId} but the confirmation mail failed.", user.Id);
            }
        }
    }
}
