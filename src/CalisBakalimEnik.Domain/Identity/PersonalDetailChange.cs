using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

/// <summary>
/// A previous value of the account's name or e-mail address (J78).
/// </summary>
/// <remarks>
/// Kept for one purpose: undoing an account takeover, where the first thing
/// an attacker changes is the address. Twelve months from the change, decided
/// 30 Sep 2026 and to be stated in the privacy text; RetentionCleanup deletes
/// older rows. The person sees their own history and gets it in the export.
/// </remarks>
public class PersonalDetailChange : BaseEntity
{
    /// <summary>FK to users. No navigation property: AppUser lives in
    /// Infrastructure and Domain cannot reference it.</summary>
    public Guid UserId { get; set; }

    /// <summary><c>displayName</c> or <c>email</c>.</summary>
    public string Field { get; set; } = string.Empty;

    public string? OldValue { get; set; }
    public string? NewValue { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}
