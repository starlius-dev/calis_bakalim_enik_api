using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

public enum MfaFactorType : short
{
    Totp = 1,
    EmailOtp = 2,

    /// <summary>
    /// Removed. SMS is the weakest second factor — SIM-swap prone — and costs
    /// money per send, while TOTP and recovery codes cover the same ground
    /// better. Nothing can enrol or verify one any more.
    /// </summary>
    /// <remarks>
    /// The NUMBER stays reserved rather than being deleted. These values are
    /// stored as smallint, so handing 3 to a future factor type would silently
    /// reinterpret any row written before the removal.
    /// </remarks>
    [Obsolete("SMS OTP was removed. The value is reserved, not reusable.")]
    SmsOtp = 3,

    RecoveryCode = 4,
}

/// <summary>
/// A user may enrol several factors; exactly one is primary.
///
/// A factor is only usable once <see cref="VerifiedAt"/> is set — enrolment has
/// to prove the factor works, otherwise a mistyped phone number locks the
/// account out permanently. See docs/DATABASE.md §4.
/// </summary>
public class MfaFactor : BaseEntity
{
    public Guid UserId { get; set; }
    public MfaFactorType FactorType { get; set; }

    /// <summary>TOTP shared secret, AES-256-GCM encrypted. NEVER plaintext.</summary>
    public byte[]? SecretEncrypted { get; set; }

    /// <summary>Email address or phone number, snapshotted at enrolment.</summary>
    public string? Destination { get; set; }

    public bool IsPrimary { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsUsable => VerifiedAt is not null && DeletedAt is null;

    /// <summary>Masked for display on the challenge screen — a stolen password
    /// must not reveal the full address or number.</summary>
    public string? MaskedDestination => FactorType switch
    {
        MfaFactorType.EmailOtp => MaskEmail(Destination),
        _ => null,
    };

    private static string? MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;

        var at = email.IndexOf('@');
        if (at <= 1) return "•••";

        var local = email[..at];
        var masked = local.Length <= 2
            ? local[0] + "•"
            : local[0] + new string('•', Math.Min(local.Length - 2, 5)) + local[^1];

        return masked + email[at..];
    }
}
