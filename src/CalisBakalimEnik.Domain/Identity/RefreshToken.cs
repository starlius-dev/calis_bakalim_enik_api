using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

public enum RefreshRevokedReason : short
{
    Rotated = 1,
    Logout = 2,
    ReuseDetected = 3,
    Admin = 4,
    PasswordChange = 5,
}

/// <summary>
/// Only the SHA-256 hash is stored; the token itself never touches the database.
///
/// <see cref="FamilyId"/> is what makes reuse detection possible: every rotation
/// stays in one family, so presenting an already-rotated token revokes the whole
/// chain. Without it a stolen refresh token is usable until expiry.
/// See docs/SECURITY.md §4.
/// </summary>
public class RefreshToken : BaseEntity
{
    /// <summary>FK to users. No navigation property: AppUser lives in
    /// Infrastructure and Domain cannot reference it.</summary>
    public Guid UserId { get; set; }

    public string TokenHash { get; set; } = string.Empty;
    public Guid FamilyId { get; set; }
    public Guid? DeviceId { get; set; }

    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public RefreshRevokedReason? RevokedReason { get; set; }
    public Guid? ReplacedById { get; set; }

    public string? CreatedIp { get; set; }
    public string? UserAgent { get; set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;
}
