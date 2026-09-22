using System.Security.Claims;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// Idempotent seeding of the three global roles and their permission claims.
/// Runs after migration, never inside one — a migration that seeds cannot be
/// re-run when the permission set changes.
/// </summary>
public sealed class DatabaseSeeder(
    RoleManager<AppRole> roles,
    UserManager<AppUser> users,
    IConfiguration configuration,
    ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedRolesAsync(ct);
        await BootstrapAdminAsync(ct);
    }

    /// <summary>
    /// Grants <see cref="Roles.PlatformAdmin"/> to the address in
    /// <c>Admin:BootstrapEmail</c>, if one is configured.
    /// </summary>
    /// <remarks>
    /// Without this there is no way to create the FIRST admin: every admin
    /// endpoint requires a permission that only an admin can grant, so the
    /// surface would be unreachable forever on a fresh database.
    ///
    /// It is opt-in, idempotent, and it will not create an account — the
    /// address must already have registered and confirmed in the normal way.
    /// Creating a user here would mean a password set outside the registration
    /// flow, which is a back door whatever it is called.
    ///
    /// It is logged at warning level on purpose. A silent privilege grant at
    /// startup is exactly what nobody notices in a log.
    /// </remarks>
    private async Task BootstrapAdminAsync(CancellationToken ct)
    {
        var email = configuration["Admin:BootstrapEmail"];
        if (string.IsNullOrWhiteSpace(email)) return;

        var user = await users.FindByEmailAsync(email.Trim());

        if (user is null)
        {
            logger.LogWarning(
                "Admin:BootstrapEmail is set to {Email} but no such account exists. " +
                "Register and confirm it first; nothing was granted.",
                email);
            return;
        }

        if (await users.IsInRoleAsync(user, Roles.PlatformAdmin)) return;

        var result = await users.AddToRoleAsync(user, Roles.PlatformAdmin);

        if (result.Succeeded)
        {
            logger.LogWarning(
                "Granted {Role} to {Email} from Admin:BootstrapEmail",
                Roles.PlatformAdmin, email);
        }
        else
        {
            logger.LogError(
                "Could not grant {Role} to {Email}: {Errors}",
                Roles.PlatformAdmin, email,
                string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }

    private async Task SeedRolesAsync(CancellationToken ct)
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
