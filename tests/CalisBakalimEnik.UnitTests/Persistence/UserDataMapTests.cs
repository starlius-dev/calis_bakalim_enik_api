using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CalisBakalimEnik.UnitTests.Persistence;

/// <summary>
/// The registry behind "Verilerimi indir" (D16) and account erasure (D15),
/// checked against the real EF model.
/// </summary>
/// <remarks>
/// Neither feature fails loudly when it is wrong. An export that misses a table
/// just returns less; an erasure that misses one just leaves data behind; an
/// erasure in the wrong order rolls back every night and nobody is ever
/// deleted. These tests are where those show up instead.
/// </remarks>
public class UserDataMapTests
{
    private sealed class NoUser : ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => false;
    }

    private static AppDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none")
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new NoUser());
    }

    private static Dictionary<string, IEntityType> TablesInModel(AppDbContext db) =>
        db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && !e.IsOwned())
            .GroupBy(e => e.GetTableName()!)
            .ToDictionary(g => g.Key, g => g.First());

    [Fact]
    public void Every_table_is_either_user_data_or_declared_not_to_be()
    {
        using var db = Context();

        var mapped = UserDataMap.Tables.Select(t => t.Table).ToHashSet();
        var unclassified = TablesInModel(db).Keys
            .Where(t => !mapped.Contains(t) && !UserDataMap.NotUserData.Contains(t))
            .ToList();

        unclassified.Should().BeEmpty(
            "a new table must be added to UserDataMap (exported and erased) or to NotUserData");
    }

    [Fact]
    public void Every_mapped_table_exists()
    {
        using var db = Context();
        var tables = TablesInModel(db);

        UserDataMap.Tables.Select(t => t.Table)
            .Concat(UserDataMap.NotUserData)
            .Where(t => !tables.ContainsKey(t))
            .Should().BeEmpty();
    }

    [Fact]
    public void Tables_are_listed_once()
    {
        UserDataMap.Tables.Select(t => t.Table).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Hidden_and_scope_columns_exist()
    {
        using var db = Context();
        var tables = TablesInModel(db);

        foreach (var table in UserDataMap.Tables)
        {
            var columns = tables[table.Table].GetProperties()
                .Select(p => p.GetColumnName())
                .ToHashSet();

            foreach (var hidden in table.Hidden)
                columns.Should().Contain(hidden, $"{table.Table} hides it from the export");

            // The column the predicate filters on (the text before the first
            // space), e.g. owner_id, user_id or the parent key.
            var scopeColumn = table.Scope.Split(' ')[0];
            columns.Should().Contain(scopeColumn, $"{table.Table} is scoped by it");
        }
    }

    /// <summary>
    /// For every foreign key between two erased tables, the table holding the
    /// key is erased first. Otherwise the DELETE of the referenced row fails on
    /// a RESTRICT key and the whole erasure rolls back, every run, forever.
    /// </summary>
    [Fact]
    public void Erasure_order_deletes_children_before_parents()
    {
        using var db = Context();
        var tables = TablesInModel(db);

        var order = UserDataMap.Tables
            .Where(t => t.Erase)
            .Select((t, i) => (t.Table, i))
            .ToDictionary(x => x.Table, x => x.i);

        foreach (var (dependent, dependentIndex) in order)
        {
            foreach (var key in tables[dependent].GetForeignKeys())
            {
                var principal = key.PrincipalEntityType.GetTableName();
                if (principal is null || principal == dependent) continue;
                if (!order.TryGetValue(principal, out var principalIndex)) continue;

                dependentIndex.Should().BeLessThan(principalIndex,
                    $"{dependent} references {principal}, so it must be erased first");
            }
        }
    }

    [Fact]
    public void Secrets_never_reach_the_export()
    {
        var hidden = UserDataMap.Tables.ToDictionary(t => t.Table, t => t.Hidden);

        hidden["mfa_factors"].Should().Contain("secret_encrypted");
        hidden["mfa_recovery_codes"].Should().Contain("code_hash");
        hidden["refresh_tokens"].Should().Contain("token_hash");
        hidden["devices"].Should().Contain("fcm_token");

        UserDataMap.Tables.Single(t => t.Table == "user_tokens").Export.Should().BeFalse(
            "Identity keeps authenticator keys there");

        UserDataMap.ProfileSql.Should().NotContainAny("password_hash", "security_stamp", "concurrency_stamp");
    }

    [Fact]
    public void Security_events_are_anonymised_not_deleted()
    {
        UserDataMap.Tables.Single(t => t.Table == "security_events").Erase.Should().BeFalse();
        UserDataMap.AnonymiseSecurityEventsSql.Should().Contain("user_id = NULL").And.Contain("erasedAt");
    }
}
