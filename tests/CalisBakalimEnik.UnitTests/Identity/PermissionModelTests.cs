using CalisBakalimEnik.Domain.Identity;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Identity;

public class PermissionModelTests
{
    [Fact]
    public void The_default_User_role_holds_no_permissions()
    {
        // Ordinary domain endpoints are guarded by ownership, not policies. A
        // "courses.write" that every authenticated user holds checks nothing.
        Roles.PermissionsFor(Roles.User).Should().BeEmpty();
    }

    [Fact]
    public void Admin_operates_the_service_but_cannot_read_another_users_content()
    {
        var admin = Roles.PermissionsFor(Roles.Admin);

        admin.Should().Contain(Permissions.UsersRead);
        admin.Should().Contain(Permissions.UsersDisable);
        admin.Should().Contain(Permissions.LogsRead);

        // There is deliberately NO permission granting read access to another
        // user's rows, and none at all to health data. The capability does not
        // exist rather than existing and being unused — an unused capability is
        // one mistaken policy attribute away from being used.
        admin.Should().NotContain(p => p.StartsWith("courses."));
        admin.Should().NotContain(p => p.StartsWith("medications."));
        admin.Should().NotContain(p => p.StartsWith("health."));

        admin.Should().NotContain(Permissions.RolesWrite,
            "an Admin that can rewrite role permissions can grant itself anything");
    }

    [Fact]
    public void PlatformAdmin_is_the_break_glass_role()
    {
        Roles.PermissionsFor(Roles.PlatformAdmin)
            .Should().BeEquivalentTo(Permissions.All);
    }

    [Fact]
    public void Every_declared_permission_is_granted_by_some_role()
    {
        var granted = new[] { Roles.User, Roles.Admin, Roles.PlatformAdmin }
            .SelectMany(Roles.PermissionsFor)
            .ToHashSet();

        Permissions.All.Should().OnlyContain(p => granted.Contains(p),
            "a permission no role grants is dead configuration");
    }

    [Fact]
    public void Permission_names_are_stable_lowercase_dotted_constants()
    {
        // These strings are persisted as role_claims rows and baked into issued
        // tokens. Renaming one silently un-grants it for every existing role.
        Permissions.All.Should().OnlyContain(
            p => p == p.ToLowerInvariant() && p.Contains('.'));
    }
}
