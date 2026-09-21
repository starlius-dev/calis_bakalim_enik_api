namespace CalisBakalimEnik.Infrastructure.Services;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// <c>Resend</c> sends real mail; anything else falls back to the local
    /// outbox file. The fallback is refused outside Development at startup —
    /// a production deployment that silently wrote confirmation links to a log
    /// file would look healthy while nobody could finish signing up.
    /// </summary>
    public string Provider { get; set; } = "Logging";

    /// <summary>Never logged, never returned by any endpoint.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// RFC 5322 sender. The domain has to be verified in Resend, otherwise every
    /// send is refused with 403 — that is the usual reason a first send fails.
    /// </summary>
    public string From { get; set; } = "Çalış Bakalım Enik <hesap@calisbakalimenik.app>";

    public string? ReplyTo { get; set; }

    /// <summary>
    /// Where the links in the mail point. The web build uses hash routing, so a
    /// confirmation link is <c>{AppBaseUrl}/#/kayit/dogrula?...</c>.
    /// </summary>
    public string AppBaseUrl { get; set; } = "https://calisbakalimenik.app";

    public bool UsesResend =>
        string.Equals(Provider, "Resend", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(ApiKey);
}
