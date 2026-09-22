using System.Security.Claims;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.Notifications;

public sealed record RegisterDeviceRequest(
    string InstallationId,
    string Platform,
    string? FcmToken,
    string? DeviceName,
    string? AppVersion,
    bool PushEnabled = true);

public sealed record DeviceResponse(
    Guid Id,
    string InstallationId,
    string Platform,
    bool PushEnabled,
    bool HasToken,
    DateTimeOffset LastSeenAt);

/// <summary>
/// Device registration for push.
/// </summary>
/// <remarks>
/// A device row is keyed by the client's stable installation id, never by the
/// FCM token — the token rotates, the device does not. See
/// docs/NOTIFICATIONS.md §5.
/// </remarks>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/devices")
            .WithTags("Devices")
            .RequireAuthorization();

        group.MapPost("/", RegisterAsync);
        group.MapGet("/", ListAsync);
        group.MapDelete("/{id:guid}", RemoveAsync);

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterDeviceRequest request,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        if (!Enum.TryParse<DevicePlatform>(request.Platform, ignoreCase: true, out var platform))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["platform"] = ["android, ios veya web olmalı."],
            });

        var now = clock.UtcNow;

        // A token belongs to exactly one row. A handed-over or reinstalled
        // phone otherwise leaves the previous owner's row pointing at it, and
        // they keep receiving the new account's notifications.
        if (!string.IsNullOrWhiteSpace(request.FcmToken))
        {
            await db.Devices
                .Where(d => d.FcmToken == request.FcmToken
                            && d.InstallationId != request.InstallationId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(d => d.FcmToken, (string?)null), ct);
        }

        var device = await db.Devices.FirstOrDefaultAsync(
            d => d.UserId == userId && d.InstallationId == request.InstallationId, ct);

        if (device is null)
        {
            device = new Device
            {
                UserId = userId.Value,
                InstallationId = request.InstallationId,
                CreatedAt = now,
            };

            db.Devices.Add(device);
        }

        device.Platform = platform;
        device.FcmToken = request.FcmToken;
        device.DeviceName = request.DeviceName;
        device.AppVersion = request.AppVersion;
        device.PushEnabled = request.PushEnabled;
        device.LastSeenAt = now;
        device.DeletedAt = null;

        await db.SaveChangesAsync(ct);

        return Results.Ok(Describe(device));
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db, ClaimsPrincipal principal, CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var devices = await db.Devices
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.LastSeenAt)
            .Take(ListLimits.Ceiling)
            .ToListAsync(ct);

        return Results.Ok(devices.Select(Describe));
    }

    /// <summary>
    /// Logout, or "stop sending to this device".
    /// </summary>
    /// <remarks>
    /// Clearing the token is the part that matters, and it is the step most
    /// often missed: the consequence is another person's reminders arriving on
    /// a handed-over phone. The row itself is kept — it is the device's
    /// history, not the session.
    /// </remarks>
    private static async Task<IResult> RemoveAsync(
        Guid id,
        AppDbContext db,
        IClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var userId = MfaEndpoints.UserId(principal);
        if (userId is null) return Results.Unauthorized();

        var updated = await db.Devices
            .Where(d => d.Id == id && d.UserId == userId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(d => d.FcmToken, (string?)null)
                      .SetProperty(d => d.PushEnabled, false)
                      .SetProperty(d => d.DeletedAt, clock.UtcNow),
                ct);

        return updated == 0 ? Results.NotFound() : Results.NoContent();
    }

    /// <summary>
    /// Never returns the token itself: it is a delivery credential, and the
    /// only thing a client needs to know is whether one is on file.
    /// </summary>
    private static DeviceResponse Describe(Device device) => new(
        device.Id,
        device.InstallationId,
        device.Platform.ToString(),
        device.PushEnabled,
        !string.IsNullOrEmpty(device.FcmToken),
        device.LastSeenAt);
}
