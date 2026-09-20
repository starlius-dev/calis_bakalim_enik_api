namespace CalisBakalimEnik.Domain.Identity;

public enum SecurityEventType : short
{
    Registered = 1,
    EmailConfirmed = 2,
    LoginAttempt = 3,
    MfaChallenge = 4,
    MfaEnrolled = 5,
    MfaRemoved = 6,
    PasswordChanged = 7,
    PasswordResetRequested = 8,
    TokenRefreshed = 9,
    RefreshReuseDetected = 10,
    AccountLocked = 11,
    RoleChanged = 12,
    AccountDeleted = 13,
    Logout = 14,
}

/// <summary>
/// Authentication outcomes, kept separate from audit_log (business changes) and
/// app_logs (diagnostics). Conflating the three is why audit trails become
/// unqueryable. See docs/DATABASE.md §6.1.
/// </summary>
public class SecurityEvent
{
    public long Id { get; set; }

    /// <summary>
    /// Null when the attempt was against an address that does not exist — which
    /// is exactly the event worth keeping, and it has no user to point at.
    /// </summary>
    public Guid? UserId { get; set; }

    public SecurityEventType EventType { get; set; }
    public bool Succeeded { get; set; }
    public string? Ip { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Structured detail as JSON. Never contains a secret.</summary>
    public string Detail { get; set; } = "{}";

    public DateTimeOffset OccurredAt { get; set; }
}
