using System.Security.Claims;
using CalisBakalimEnik.Application.Common.Interfaces;

namespace CalisBakalimEnik.Api.Services;

/// <summary>
/// Reads the current user from the validated JWT only. Never from a header or body —
/// see docs/SECURITY.md §2. Real claims arrive in Phase 3; the shape is fixed now so
/// the ownership filter and audit interceptor have something to depend on.
/// </summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public Guid? Id =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
                      ?? Principal?.FindFirstValue("sub"), out var id)
            ? id
            : null;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public IReadOnlyCollection<string> Permissions =>
        Principal?.FindAll("perm").Select(c => c.Value).ToArray() ?? [];

    public bool HasPermission(string permission) => Permissions.Contains(permission);
}
