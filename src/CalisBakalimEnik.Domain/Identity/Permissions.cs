namespace CalisBakalimEnik.Domain.Identity;

/// <summary>
/// Permissions exist for the ADMIN surface only.
///
/// Ordinary domain endpoints need no permission: the global query filter on
/// OwnerId already means a row belonging to someone else is invisible, and a
/// "courses.write" policy every authenticated user holds checks nothing.
/// See docs/SECURITY.md §7.
/// </summary>
public static class Permissions
{
    public const string ClaimType = "perm";

    public const string UsersRead = "users.read";
    public const string UsersDisable = "users.disable";
    public const string UsersRoles = "users.roles";
    public const string RolesRead = "roles.read";
    public const string RolesWrite = "roles.write";
    public const string LogsRead = "logs.read";
    public const string CatalogueManage = "catalogue.manage";

    public static readonly IReadOnlyList<string> All =
    [
        UsersRead, UsersDisable, UsersRoles,
        RolesRead, RolesWrite,
        LogsRead, CatalogueManage,
    ];
}

public static class Roles
{
    public const string User = "User";
    public const string Admin = "Admin";
    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>
    /// Admin operates the service; it is NOT a supervisor of other people's
    /// content. There is deliberately no permission that grants read access to
    /// another user's rows, and none at all to health data.
    /// </summary>
    public static IReadOnlyList<string> PermissionsFor(string role) => role switch
    {
        Admin =>
        [
            Permissions.UsersRead, Permissions.UsersDisable, Permissions.UsersRoles,
            Permissions.RolesRead, Permissions.LogsRead, Permissions.CatalogueManage,
        ],
        PlatformAdmin => Permissions.All,
        _ => [],
    };
}
