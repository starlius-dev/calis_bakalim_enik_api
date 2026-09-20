using CalisBakalimEnik.Domain.Common;

namespace CalisBakalimEnik.Domain.Identity;

public enum DevicePlatform : short
{
    Android = 1,
    Ios = 2,
    Web = 3,
}

/// <summary>
/// Keyed by a client-generated stable id in secure storage, NOT by the FCM token —
/// the token rotates, the device does not. The unique index on FcmToken is global
/// so a reinstalled phone cannot leave two rows pointing at one token, which would
/// send the previous owner's notifications to the new account.
/// </summary>
public class Device : BaseEntity
{
    /// <summary>FK to users. No navigation property: AppUser lives in
    /// Infrastructure and Domain cannot reference it.</summary>
    public Guid UserId { get; set; }

    /// <summary>Stable per-install id supplied by the client.</summary>
    public string InstallationId { get; set; } = string.Empty;

    public DevicePlatform Platform { get; set; }
    public string? FcmToken { get; set; }
    public string? DeviceName { get; set; }
    public string? AppVersion { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
    public bool PushEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
