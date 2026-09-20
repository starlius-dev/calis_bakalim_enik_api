using Microsoft.AspNetCore.Identity;

namespace CalisBakalimEnik.Infrastructure.Identity;

public enum UserStatus : short
{
    Active = 1,
    PendingConfirmation = 2,
    Disabled = 3,
}

/// <summary>
/// There is no tenant: email is a GLOBAL identity, so Identity's own unique
/// indexes on UserName and Email are correct and are deliberately left in place.
/// See docs/DATABASE.md §3.
///
/// This type lives in INFRASTRUCTURE, not Domain, because it derives from
/// IdentityUser&lt;Guid&gt; in Microsoft.AspNetCore.Identity — and Domain
/// references nothing. Domain entities hold a bare UserId instead of a
/// navigation property, which is also the cleaner model.
/// </summary>
public class AppUser : IdentityUser<Guid>
{
    public AppUser() => Id = Guid.CreateVersion7();

    public string DisplayName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Locale { get; set; } = "tr";
    public string TimeZone { get; set; } = "Europe/Istanbul";

    /// <summary>Registration is open, so an account starts unable to log in.</summary>
    public UserStatus Status { get; set; } = UserStatus.PendingConfirmation;

    public bool MfaRequired { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Soft delete; a purge job destroys the rows after a grace window.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
