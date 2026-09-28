using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Permanently erases one account (D15): every row in <see cref="UserDataMap"/>,
/// the user row last, security events anonymised rather than deleted.
/// </summary>
/// <remarks>
/// One transaction. If any statement fails (a foreign key the map does not
/// know about, a dropped connection) nothing is erased and the next run of
/// <see cref="AccountErasureJob"/> tries again; half an account is the one
/// outcome that must never happen.
/// </remarks>
public sealed class AccountErasure(AppDbContext db, IClock clock, ILogger<AccountErasure> logger)
{
    public async Task EraseAsync(Guid userId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var anonymised = await db.Database.ExecuteSqlRawAsync(
            UserDataMap.AnonymiseSecurityEventsSql,
            [new NpgsqlParameter("uid", userId), new NpgsqlParameter("now", clock.UtcNow)],
            ct);

        var total = 0;
        foreach (var table in UserDataMap.Tables.Where(t => t.Erase))
            total += await db.Database.ExecuteSqlRawAsync(
                UserDataMap.EraseSql(table), [new NpgsqlParameter("uid", userId)], ct);

        var users = await db.Database.ExecuteSqlRawAsync(
            UserDataMap.EraseUserSql, [new NpgsqlParameter("uid", userId)], ct);

        await transaction.CommitAsync(ct);

        logger.LogWarning(
            "Erased account {UserId}: {Rows} rows deleted, {Events} security events anonymised, user row {User}",
            userId, total, anonymised, users == 1 ? "deleted" : "already gone");
    }
}
