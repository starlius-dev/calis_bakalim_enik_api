namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// How long each table that grows without bound is kept — docs/DATABASE.md §6.
/// </summary>
/// <remarks>
/// Every window here is in days, and <b>0 means never delete</b> rather than
/// delete everything. That choice is deliberate: the failure mode of a missing
/// or mistyped setting should be "the table keeps growing", which is slow and
/// visible, not "the table is emptied tonight", which is fast and final.
/// </remarks>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Sweep once at startup, in addition to the nightly run.
    /// </summary>
    /// <remarks>
    /// Off by default. It exists so a window that was just changed can be
    /// applied without waiting until 03:00, and so the statements can be
    /// exercised against a real schema on demand — a column renamed out from
    /// under this job otherwise fails silently in the middle of the night.
    /// </remarks>
    public bool RunOnStartup { get; set; }

    /// <summary>
    /// The hour, UTC, at which the sweep runs. Off-peak because a large first
    /// sweep holds locks on the log tables while it works.
    /// </summary>
    public int RunAtHourUtc { get; set; } = 3;

    /// <summary>
    /// Rows per statement. Deleting a year of history in one statement takes a
    /// lock for the whole of it and bloats the WAL; a loop of small deletes
    /// lets other work through between batches.
    /// </summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>
    /// Authentication history. A year because that is the window an incident is
    /// still investigated in — "when did this account last change its
    /// password" stops being answerable the moment this is too short.
    /// </summary>
    public int SecurityEventDays { get; set; } = 365;

    /// <summary>
    /// Dead refresh tokens, counted from the later of expiry and revocation.
    /// </summary>
    /// <remarks>
    /// <b>The delay is load-bearing, not housekeeping.</b> Reuse detection
    /// works by finding a row whose <c>revoked_at</c> is set: a rotated token
    /// presented again is how a leaked chain is caught. Delete that row and the
    /// same request looks like an unknown token — refused, but refused
    /// quietly, with no family revocation and no security event. The sweep
    /// therefore only touches tokens that have also EXPIRED, after which the
    /// token could not have been accepted anyway.
    /// </remarks>
    public int RefreshTokenDays { get; set; } = 90;

    /// <summary>
    /// Outbox rows the processor has already sent. It marks
    /// <c>processed_at</c> and moves on, so without this they are kept forever
    /// — one row per notification ever delivered.
    /// </summary>
    public int ProcessedOutboxDays { get; set; } = 7;

    /// <summary>
    /// Read notifications. <b>Disabled by default (0)</b>: unlike the others
    /// this is a user's own inbox rather than a log, docs/DATABASE.md §6 sets
    /// no policy for it, and nobody has asked for old notifications to
    /// disappear. Deliveries go with them — the FK cascades.
    /// </summary>
    public int ReadNotificationDays { get; set; }
}
