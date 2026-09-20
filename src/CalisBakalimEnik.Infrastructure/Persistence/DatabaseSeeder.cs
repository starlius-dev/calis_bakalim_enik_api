using System.Security.Claims;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Idempotent seeding of the three global roles and their permission claims.
/// Runs after migration, never inside one — a migration that seeds cannot be
/// re-run when the permission set changes.
/// </summary>
public sealed class DatabaseSeeder(
    RoleManager<AppRole> roles,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var roleName in new[] { Roles.User, Roles.Admin, Roles.PlatformAdmin })
        {
            var role = await roles.FindByNameAsync(roleName);

            if (role is null)
            {
                role = new AppRole(roleName)
                {
                    IsSystem = true,
                    Description = DescriptionFor(roleName),
                };

                var created = await roles.CreateAsync(role);
                if (!created.Succeeded)
                {
                    logger.LogError("Could not create role {Role}: {Errors}",
                        roleName, string.Join("; ", created.Errors.Select(e => e.Description)));
                    continue;
                }

                logger.LogInformation("Seeded role {Role}", roleName);
            }

            await SyncPermissionsAsync(role, Roles.PermissionsFor(roleName));
        }
    }

    /// <summary>
    /// Adds missing permission claims and removes ones no longer granted, so
    /// tightening a role in code actually tightens it in the database.
    /// </summary>
    private async Task SyncPermissionsAsync(AppRole role, IReadOnlyList<string> desired)
    {
        var existing = (await roles.GetClaimsAsync(role))
            .Where(c => c.Type == Permissions.ClaimType)
            .ToList();

        foreach (var claim in existing.Where(c => !desired.Contains(c.Value)))
        {
            await roles.RemoveClaimAsync(role, claim);
            logger.LogInformation("Revoked {Permission} from {Role}", claim.Value, role.Name);
        }

        var have = existing.Select(c => c.Value).ToHashSet();

        foreach (var permission in desired.Where(p => !have.Contains(p)))
        {
            await roles.AddClaimAsync(role, new Claim(Permissions.ClaimType, permission));
            logger.LogInformation("Granted {Permission} to {Role}", permission, role.Name);
        }
    }

    private static string DescriptionFor(string role) => role switch
    {
        Roles.User => "Default role. Full control of its own data and nothing else.",
        Roles.Admin => "Operates the service. Cannot read another user's content or health data.",
        Roles.PlatformAdmin => "Break-glass: migrations, retention, catalogue seeding.",
        _ => string.Empty,
    };
}
