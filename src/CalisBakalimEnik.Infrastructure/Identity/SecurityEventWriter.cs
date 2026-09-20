using System.Text.Json;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Domain.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;

namespace CalisBakalimEnik.Infrastructure.Identity;

/// <summary>
/// Every authentication outcome is recorded — successes as well as failures.
/// A trail that only has failures cannot answer "was this login legitimate?".
/// Never writes a secret: no passwords, codes, tokens or TOTP secrets.
/// </summary>
public sealed class SecurityEventWriter(AppDbContext db, IClock clock)
{
    public async Task WriteAsync(
        SecurityEventType type,
        bool succeeded,
        Guid? userId,
        string? ip,
        string? userAgent,
        object? detail = null,
        CancellationToken ct = default)
    {
        db.SecurityEvents.Add(new SecurityEvent
        {
            UserId = userId,
            EventType = type,
            Succeeded = succeeded,
            Ip = ip,
            UserAgent = userAgent,
            Detail = detail is null ? "{}" : JsonSerializer.Serialize(detail),
            OccurredAt = clock.UtcNow,
        });

        await db.SaveChangesAsync(ct);
    }
}
