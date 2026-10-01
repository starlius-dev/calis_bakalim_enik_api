using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Diagnostics;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace CalisBakalimEnik.Api.Features.System;

/// <param name="Kind"><c>flutter</c> or <c>async</c>.</param>
/// <param name="CorrelationId">The last X-Correlation-Id the app saw.</param>
public sealed record ClientErrorRequest(
    string? Kind,
    string? Message,
    string? Stack,
    string? Route,
    string? CorrelationId);

public sealed record ClientErrorSummary(
    long Id,
    DateTimeOffset ReceivedAt,
    string Platform,
    string AppVersion,
    string Kind,
    string Message,
    string? Route,
    Guid? UserId,
    string? UserEmail,
    Guid? InstallationId);

public sealed record ClientErrorDetail(
    long Id,
    DateTimeOffset ReceivedAt,
    string Platform,
    string AppVersion,
    string Kind,
    string Message,
    string? Stack,
    string? Route,
    string? CorrelationId,
    string? Locale,
    string? UserAgent,
    Guid? UserId,
    string? UserEmail,
    Guid? InstallationId);

public sealed record ClientErrorPage(IReadOnlyList<ClientErrorSummary> Items, long? NextCursor);

/// <summary>
/// Uncaught errors in the app, reported by the app itself (D19), and the
/// admin view of them.
/// </summary>
/// <remarks>
/// Ours rather than a third-party crash service: no new processor to name in
/// the privacy text (KVKK), nothing leaves this server, and the app is
/// online-only anyway. The report is accepted without sign-in, because the
/// errors worth seeing include the ones that stop someone from signing in;
/// the rate limit and the size caps are what keep that safe.
/// </remarks>
public static class ClientErrorEndpoints
{
    public static readonly IReadOnlySet<string> Kinds =
        new HashSet<string> { "flutter", "async" };

    public static readonly IReadOnlySet<string> Platforms =
        new HashSet<string> { "web", "android", "ios" };

    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapClientErrorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/client-errors", ReportAsync)
            .AllowAnonymous()
            .RateLimit(RateLimitGuard.Policies.ClientErrors)
            .WithTags("System");

        var admin = app.MapGroup("/api/v1/admin/client-errors").WithTags("Admin");
        admin.MapGet("/", ListAsync).RequireAuthorization(Permissions.LogsRead);
        admin.MapGet("/{id:long}", GetAsync).RequireAuthorization(Permissions.LogsRead);

        return app;
    }

    /// <summary>
    /// Turns what the app sent into a row, or null when it is not a report.
    /// </summary>
    /// <remarks>
    /// Over-long fields are cut, not refused: a stack trace that runs long is
    /// still the most useful thing in the report, and a refusal would lose the
    /// whole of it. Only a missing message or an unknown kind is refused.
    /// </remarks>
    public static ClientErrorReport? Build(
        ClientErrorRequest request,
        string? platform,
        string? appVersion,
        string? installationId,
        string? acceptLanguage,
        string? userAgent,
        Guid? userId,
        DateTimeOffset now)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        var message = request.Message?.Trim();

        if (kind is null || !Kinds.Contains(kind)) return null;
        if (string.IsNullOrEmpty(message)) return null;

        var p = platform?.Trim().ToLowerInvariant();

        return new ClientErrorReport
        {
            UserId = userId,
            InstallationId = Guid.TryParse(installationId, out var install) ? install : null,
            Platform = p is not null && Platforms.Contains(p) ? p : "unknown",
            AppVersion = Clip(appVersion?.Trim(), 40) is { Length: > 0 } v ? v : "unknown",
            Kind = kind,
            Message = Clip(message, ClientErrorReportConfiguration.MessageLength)!,
            Stack = Clip(request.Stack, ClientErrorReportConfiguration.StackLength),
            Route = Clip(request.Route?.Trim(), ClientErrorReportConfiguration.RouteLength),
            CorrelationId = Clip(request.CorrelationId?.Trim(), 64),
            Locale = Clip(acceptLanguage?.Split(',')[0].Trim(), 16),
            UserAgent = Clip(userAgent, 400),
            ReceivedAt = now,
        };
    }

    private static string? Clip(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length <= max ? value : value[..max];

    private static async Task<IResult> ReportAsync(
        ClientErrorRequest request,
        HttpContext http,
        AppDbContext db,
        IClock clock,
        ICurrentUser user,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var headers = http.Request.Headers;
        var report = Build(
            request,
            headers["X-Client-Platform"].ToString(),
            headers["X-Client-Version"].ToString(),
            headers["X-Installation-Id"].ToString(),
            headers.AcceptLanguage.ToString(),
            headers.UserAgent.ToString(),
            user.Id,
            clock.UtcNow);

        if (report is null) return Results.BadRequest();

        db.ClientErrorReports.Add(report);
        await db.SaveChangesAsync(ct);

        // A line in the service log so a failing build shows up where the rest
        // of the evidence is. The message itself stays out of the journal,
        // which is kept far longer than the report; it is read in the admin
        // screen, where it ages out after 90 days.
        loggers.CreateLogger("ClientErrors").LogWarning(
            "App error report {ReportId}: {Kind} on {Platform} {AppVersion}",
            report.Id, report.Kind, report.Platform, report.AppVersion);

        return Results.Accepted();
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db,
        CancellationToken ct,
        string? platform = null,
        string? version = null,
        long? before = null,
        int take = 50)
    {
        var size = Math.Clamp(take, 1, MaxPageSize);
        var query = db.ClientErrorReports.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(platform))
            query = query.Where(r => r.Platform == platform.Trim().ToLower());
        if (!string.IsNullOrWhiteSpace(version))
            query = query.Where(r => r.AppVersion == version.Trim());
        if (before is not null)
            query = query.Where(r => r.Id < before);

        var page = await query
            .OrderByDescending(r => r.Id)
            .Take(size)
            .ToListAsync(ct);

        var emails = await EmailsAsync(db, page.Select(r => r.UserId), ct);

        var items = page.Select(r => new ClientErrorSummary(
            r.Id, r.ReceivedAt, r.Platform, r.AppVersion, r.Kind,
            FirstLine(r.Message), r.Route, r.UserId,
            r.UserId is null ? null : emails.GetValueOrDefault(r.UserId.Value),
            r.InstallationId)).ToList();

        return Results.Ok(new ClientErrorPage(items, page.Count == size ? page[^1].Id : null));
    }

    private static async Task<IResult> GetAsync(long id, AppDbContext db, CancellationToken ct)
    {
        var r = await db.ClientErrorReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (r is null) return Results.NotFound();

        var emails = await EmailsAsync(db, [r.UserId], ct);

        return Results.Ok(new ClientErrorDetail(
            r.Id, r.ReceivedAt, r.Platform, r.AppVersion, r.Kind, r.Message, r.Stack,
            r.Route, r.CorrelationId, r.Locale, r.UserAgent, r.UserId,
            r.UserId is null ? null : emails.GetValueOrDefault(r.UserId.Value),
            r.InstallationId));
    }

    /// <summary>The list shows one line per report; the whole message and
    /// the stack are on the detail.</summary>
    public static string FirstLine(string message)
    {
        var end = message.IndexOf('\n');
        var line = end < 0 ? message : message[..end];
        return line.Length <= 300 ? line.TrimEnd() : line[..300];
    }

    private static Task<Dictionary<Guid, string>> EmailsAsync(
        AppDbContext db, IEnumerable<Guid?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email ?? string.Empty, ct);
    }
}
