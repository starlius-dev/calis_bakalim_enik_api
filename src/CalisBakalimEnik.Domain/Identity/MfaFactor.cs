using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

public enum MfaFactorType : short
{
    Totp = 1,
    EmailOtp = 2,
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
        MfaFactorType.SmsOtp => MaskPhone(Destination),
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

    private static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Length < 4) return "•••";
        return string.Concat(new string('•', phone.Length - 4).AsSpan(), phone.AsSpan(phone.Length - 4));
    }
}
