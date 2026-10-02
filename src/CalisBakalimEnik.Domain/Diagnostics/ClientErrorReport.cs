namespace CalisBakalimEnik.Domain.Diagnostics;

/// <summary>
/// An uncaught error in the app, as the app reported it (D19).
/// </summary>
/// <remarks>
/// Before this, a tester hitting a Dart exception produced nothing anyone could
/// see. Kept 90 days (RetentionOptions.ClientErrorReportDays): long enough to
/// see whether a fix held across a few releases, short because a message can
/// carry fragments of what the person was doing. Exported and erased with the
/// account through UserDataMap.
/// </remarks>
public class ClientErrorReport
{
    public long Id { get; set; }

    /// <summary>Null when the error happened before sign-in.</summary>
    public Guid? UserId { get; set; }

    /// <summary>The install that sent it (X-Installation-Id), so one device's
    /// repeated crash reads as one problem, not many.</summary>
    public Guid? InstallationId { get; set; }

    /// <summary><c>web</c>, <c>android</c> or <c>ios</c>.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>The app build, e.g. <c>0.1.0+20</c>.</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// Where it was caught: <c>flutter</c> (a framework error, usually during
    /// build or layout) or <c>async</c> (an error nothing awaited).
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }

    /// <summary>The screen the person was on, as the router names it.</summary>
    public string? Route { get; set; }

    /// <summary>The last API correlation id the app saw: the link to the
    /// server's own log of the same moment.</summary>
    public string? CorrelationId { get; set; }

    public string? Locale { get; set; }
    public string? UserAgent { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
