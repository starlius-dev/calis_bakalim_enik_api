using Microsoft.AspNetCore.Identity;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Roles are GLOBAL — not scoped to anything, because there is no tenant.
/// Authorization is by permission; a role is just a bundle of them.
/// </summary>
public class AppRole : IdentityRole<Guid>
{
    public AppRole() => Id = Guid.CreateVersion7();

    public AppRole(string name) : this()
    {
        Name = name;
        NormalizedName = name.ToUpperInvariant();
    }

    public string? Description { get; set; }
    public bool IsSystem { get; set; }
}
