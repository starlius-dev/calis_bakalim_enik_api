using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

/// <summary>
/// Single-use, so each code is its own row and is burnt individually.
/// Displayed exactly once at generation and never retrievable afterwards.
/// </summary>
public class MfaRecoveryCode : BaseEntity
{
    public Guid UserId { get; set; }

    /// <summary>Argon2id. The code itself is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTimeOffset? UsedAt { get; set; }
    public string? UsedIp { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
