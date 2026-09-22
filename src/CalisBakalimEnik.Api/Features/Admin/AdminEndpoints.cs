using System.Security.Claims;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Admin;

public sealed record AdminUserResponse(
    Guid Id,
    string Email,
    string DisplayName,
    string Status,
    IReadOnlyCollection<string> Roles,
    bool MfaRequired,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record UserPage(
    IReadOnlyList<AdminUserResponse> Items,
    DateTimeOffset? NextCursor);

public sealed record SetRolesRequest(IReadOnlyList<string> Roles);

public sealed record SecurityEventResponse(
    long Id,
    Guid? UserId,
    string? Email,
    string EventType,
    bool Succeeded,
    string? Ip,
    string? UserAgent,
    string Detail,
    DateTimeOffset OccurredAt);

public sealed record SecurityEventPage(
    IReadOnlyList<SecurityEventResponse> Items,
    long? NextCursor);

public sealed record RoleResponse(string Name, IReadOnlyList<string> Permissions);

/// <summary>
/// The admin surface — the only place permissions do any work.
/// </summary>
/// <remarks>
/// <b>Admin operates the service; it is not a supervisor of other people's
/// content.</b> Everything here is about accounts and the audit trail. There is
/// deliberately no endpoint that reads another user's tasks, courses or notes,
/// none at all that touches health data, and no impersonation. See
/// docs/SECURITY.md §7.
///
/// Note what is absent: no "reset this user's password", because that is an
/// account takeover with a friendly name, and no "read their MFA secrets". An
/// admin who needs to help someone locked out disables the account or waits
/// out the lockout; the user resets their own password by email.
/// </remarks>
public static class AdminEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/v1/admin/users").WithTags("Admin");

        users.MapGet("/", ListUsersAsync)
            .RequireAuthorization(Permissions.UsersRead);

        users.MapGet("/{id:guid}", GetUserAsync)
            .RequireAuthorization(Permissions.UsersRead);

        users.MapPost("/{id:guid}/disable", DisableAsync)
            .RequireAuthorization(Permissions.UsersDisable);

        users.MapPost("/{id:guid}/enable", EnableAsync)
            .RequireAuthorization(Permissions.UsersDisable);

        users.MapPut("/{id:guid}/roles", SetRolesAsync)
            .RequireAuthorization(Permissions.UsersRoles);

        var roles = app.MapGroup("/api/v1/admin/roles").WithTags("Admin");
        roles.MapGet("/", ListRolesAsync).RequireAuthorization(Permissions.RolesRead);

        var events = app.MapGroup("/api/v1/admin/security-events").WithTags("Admin");
        events.MapGet("/", ListEventsAsync).RequireAuthorization(Permissions.LogsRead);

        return app;
    }

    // ── users ────────────────────────────────────────────────────────────

    /// <summary>
    /// Keyset-paged on <c>CreatedAt</c> descending, not OFFSET.
    /// </summary>
    /// <remarks>
    /// An offset page drifts as rows are inserted — a registration during
    /// paging silently repeats or skips a user — and it degrades as the table
    /// grows. The cursor is the last row's <c>CreatedAt</c>.
    /// </remarks>
    private static async Task<IResult> ListUsersAsync(
        AppDbContext db,
        UserManager<AppUser> users,
        CancellationToken ct,
        string? q = null,
        string? status = null,
        DateTimeOffset? before = null,
        int take = 50)
    {
        var size = Math.Clamp(take, 1, MaxPageSize);
        var query = db.Users.AsNoTracking().Where(u => u.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(u =>
                u.Email!.ToLower().Contains(term) ||
                u.DisplayName.ToLower().Contains(term));
        }

        if (status is not null && Enum.TryParse<UserStatus>(status, true, out var wanted))
            query = query.Where(u => u.Status == wanted);

        if (before is not null) query = query.Where(u => u.CreatedAt < before);

        var page = await query
            .OrderByDescending(u => u.CreatedAt)
            .Take(size)
            .ToListAsync(ct);

        var described = new List<AdminUserResponse>(page.Count);
        foreach (var user in page) described.Add(await DescribeAsync(users, user));

        return Results.Ok(new UserPage(
            described,
            page.Count == size ? page[^1].CreatedAt : null));
    }

    private static async Task<IResult> GetUserAsync(
        Guid id, AppDbContext db, UserManager<AppUser> users, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id && u.DeletedAt == null, ct);

        return user is null
            ? Results.NotFound()
            : Results.Ok(await DescribeAsync(users, user));
    }

    /// <summary>
    /// Disables an account and cuts its sessions.
    /// </summary>
    /// <remarks>
    /// Revoking the refresh tokens is the point. Leaving them alive means the
    /// account keeps working for the life of its access token and can renew
    /// indefinitely — a "disabled" user who is still signed in is not disabled.
    /// </remarks>
    private static async Task<IResult> DisableAsync(
        Guid id,
        AppDbContext db,
        AuthService auth,
        SecurityEventWriter events,
        IClock clock,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var actor = MfaEndpoints.UserId(principal);
        if (actor is null) return Results.Unauthorized();

        // An admin who disables themselves is locked out of the surface that
        // would let them undo it.
        if (actor == id)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["Kendi hesabını devre dışı bırakamazsın."],
            });
        }

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == id && u.DeletedAt == null, ct);

        if (user is null) return Results.NotFound();

        user.Status = UserStatus.Disabled;
        await db.SaveChangesAsync(ct);
        await auth.RevokeAllForUserAsync(id, RefreshRevokedReason.Admin, ct);

        await events.WriteAsync(
            SecurityEventType.RoleChanged, true, id,
            ClientIp(http), http.Request.Headers.UserAgent.ToString(),
            new { action = "disabled", by = actor }, ct);

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> EnableAsync(
        Guid id,
        AppDbContext db,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var actor = MfaEndpoints.UserId(principal);
        if (actor is null) return Results.Unauthorized();

        var user = await db.Users.FirstOrDefaultAsync(
            u => u.Id == id && u.DeletedAt == null, ct);

        if (user is null) return Results.NotFound();

        // Back to PendingConfirmation if they never confirmed: enabling must
        // not quietly grant what confirmation was there to prove.
        user.Status = user.EmailConfirmed
            ? UserStatus.Active
            : UserStatus.PendingConfirmation;

        await events.WriteAsync(
            SecurityEventType.RoleChanged, true, id,
            ClientIp(http), http.Request.Headers.UserAgent.ToString(),
            new { action = "enabled", by = actor }, ct);

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Replaces a user's roles.
    /// </summary>
    /// <remarks>
    /// The permissions in a JWT are projected at sign-in, so a role change does
    /// not reach an existing token. The sessions are revoked for that reason:
    /// otherwise a demoted admin keeps admin rights until their access token
    /// expires, which is exactly the window that matters.
    /// </remarks>
    private static async Task<IResult> SetRolesAsync(
        Guid id,
        SetRolesRequest request,
        AppDbContext db,
        UserManager<AppUser> users,
        AuthService auth,
        SecurityEventWriter events,
        ClaimsPrincipal principal,
        HttpContext http,
        CancellationToken ct)
    {
        var actor = MfaEndpoints.UserId(principal);
        if (actor is null) return Results.Unauthorized();

        var wanted = request.Roles
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = new[] { Roles.User, Roles.Admin, Roles.PlatformAdmin };
        var unknown = wanted
            .Where(r => !known.Contains(r, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (unknown.Count > 0)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["roles"] = [$"Bilinmeyen rol: {string.Join(", ", unknown)}."],
            });
        }

        // Removing your own admin rights leaves nobody able to give them back
        // unless another admin exists — and the common case is that one does
        // not.
        if (actor == id)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["id"] = ["Kendi rollerini değiştiremezsin."],
            });
        }

        var user = await users.FindByIdAsync(id.ToString());
        if (user is null || user.DeletedAt is not null) return Results.NotFound();

        var current = await users.GetRolesAsync(user);
        var removed = await users.RemoveFromRolesAsync(user, current);
        if (!removed.Succeeded) return Problem(removed);

        if (wanted.Count > 0)
        {
            var added = await users.AddToRolesAsync(user, wanted);
            if (!added.Succeeded) return Problem(added);
        }

        // The permissions in a JWT are projected at sign-in, so the change
        // does not reach an existing token until the session is cut.
        await auth.RevokeAllForUserAsync(id, RefreshRevokedReason.Admin, ct);

        await events.WriteAsync(
            SecurityEventType.RoleChanged, true, id,
            ClientIp(http), http.Request.Headers.UserAgent.ToString(),
            new { from = current, to = wanted, by = actor }, ct);

        await db.SaveChangesAsync(ct);
        return Results.Ok(await DescribeAsync(users, user));
    }

    // ── roles ────────────────────────────────────────────────────────────

    private static IResult ListRolesAsync() => Results.Ok(new[]
    {
        new RoleResponse(Roles.User, Roles.PermissionsFor(Roles.User).ToList()),
        new RoleResponse(Roles.Admin, Roles.PermissionsFor(Roles.Admin).ToList()),
        new RoleResponse(
            Roles.PlatformAdmin, Roles.PermissionsFor(Roles.PlatformAdmin).ToList()),
    });

    // ── security events ──────────────────────────────────────────────────

    /// <summary>
    /// The audit trail for authentication outcomes.
    /// </summary>
    /// <remarks>
    /// Keyset-paged on the identity, which is monotonic and unique — unlike
    /// <c>OccurredAt</c>, where several events in the same millisecond would
    /// straddle a page boundary and one would be lost.
    /// </remarks>
    private static async Task<IResult> ListEventsAsync(
        AppDbContext db,
        CancellationToken ct,
        Guid? userId = null,
        string? type = null,
        bool? succeeded = null,
        long? before = null,
        int take = 50)
    {
        var size = Math.Clamp(take, 1, MaxPageSize);
        var query = db.SecurityEvents.AsNoTracking().AsQueryable();

        if (userId is not null) query = query.Where(e => e.UserId == userId);
        if (succeeded is not null) query = query.Where(e => e.Succeeded == succeeded);
        if (before is not null) query = query.Where(e => e.Id < before);

        if (type is not null && Enum.TryParse<SecurityEventType>(type, true, out var wanted))
            query = query.Where(e => e.EventType == wanted);

        var page = await query
            .OrderByDescending(e => e.Id)
            .Take(size)
            .ToListAsync(ct);

        // One lookup for the whole page rather than a join per row: the same
        // handful of users account for most events.
        var ids = page.Where(e => e.UserId != null).Select(e => e.UserId!.Value).Distinct();
        var emails = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? string.Empty, ct);

        var items = page.Select(e => new SecurityEventResponse(
            e.Id,
            e.UserId,
            e.UserId is null ? null : emails.GetValueOrDefault(e.UserId.Value),
            e.EventType.ToString(),
            e.Succeeded,
            e.Ip,
            e.UserAgent,
            e.Detail,
            e.OccurredAt)).ToList();

        return Results.Ok(new SecurityEventPage(
            items, page.Count == size ? page[^1].Id : null));
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static async Task<AdminUserResponse> DescribeAsync(
        UserManager<AppUser> users, AppUser user) =>
        new(
            user.Id,
            user.Email ?? string.Empty,
            user.DisplayName,
            user.Status.ToString(),
            (await users.GetRolesAsync(user)).ToList(),
            user.MfaRequired,
            user.CreatedAt,
            user.LastLoginAt);

    private static IResult Problem(IdentityResult result) =>
        Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["roles"] = [string.Join(" ", result.Errors.Select(e => e.Description))],
        });

    private static string? ClientIp(HttpContext http) =>
        http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf)
        && !string.IsNullOrWhiteSpace(cf)
            ? cf.ToString()
            : http.Connection.RemoteIpAddress?.ToString();
}
