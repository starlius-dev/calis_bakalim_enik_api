using CalisBakalimEnik.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Used by `dotnet ef` only. Migrations connect as the MIGRATIONS role, which owns
/// the schema — the running app connects as the app role and cannot issue DDL.
/// See docs/DEPLOYMENT.md §3.
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <summary>Matches UserSecretsId in the Api project file.</summary>
    private const string UserSecretsId = "75502c24-a134-43f4-adde-20d7a4876780";

    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "..", "CalisBakalimEnik.Api"))
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddUserSecrets(UserSecretsId)
            .AddEnvironmentVariables()
            .Build();

        var connectionString =
            configuration.GetConnectionString("Migrations")
            ?? configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "No connection string. Set ConnectionStrings:Migrations in user-secrets.");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options, new DesignTimeCurrentUser());
    }

    /// <summary>No HTTP request at design time, so no current user.</summary>
    private sealed class DesignTimeCurrentUser : ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAuthenticated => false;
        public IReadOnlyCollection<string> Permissions => [];
        public bool HasPermission(string permission) => false;
    }
}
