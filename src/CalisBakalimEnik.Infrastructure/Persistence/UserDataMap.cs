namespace CalisBakalimEnik.Infrastructure.Persistence;

/// <summary>
/// One table that holds a person's data, and how to find their rows in it.
/// </summary>
/// <param name="Table">The table name. A constant, never user input.</param>
/// <param name="Area">The folder it goes into in the export ZIP.</param>
/// <param name="Scope">
/// SQL predicate selecting the person's rows, with the user id as <c>@uid</c>.
/// Explicit, not the global query filter: the erasure job has no signed-in
/// user, and soft-deleted rows are still stored data that must be exported
/// and erased like the rest.
/// </param>
/// <param name="Hidden">
/// Columns never exported: secrets (hashes, encrypted MFA secrets, push
/// tokens). The person's own data is not hidden.
/// </param>
/// <param name="Export">False for rows that mean nothing outside the system.</param>
/// <param name="Erase">
/// False where erasure is not a DELETE (security_events are anonymised).
/// </param>
public sealed record UserTable(
    string Table,
    string Area,
    string Scope,
    string[] Hidden,
    bool Export = true,
    bool Erase = true);

/// <summary>
/// Every table holding a person's data (D15 account deletion, D16 export).
/// </summary>
/// <remarks>
/// <para><b>The order is the erasure order</b>: children before parents, so no
/// foreign key is ever left pointing at a deleted row. Several of those keys
/// are RESTRICT (workout sets and plan items to exercises, meal items to
/// foods), which is why a person's custom exercises and foods come after
/// everything that uses them.</para>
///
/// <para>A new table must be added here or to <see cref="NotUserData"/>;
/// UserDataMapTests fails otherwise. That test is the point: an export that
/// silently misses a table breaks the right of access, and an erasure that
/// misses one breaks the right to be forgotten, and neither shows up anywhere
/// else.</para>
/// </remarks>
public static class UserDataMap
{
    private const string Owner = "owner_id = @uid";
    private const string User = "user_id = @uid";
    private static readonly string[] None = [];

    private static string ChildOf(string column, string parent, string parentScope = Owner) =>
        $"{column} IN (SELECT id FROM {parent} WHERE {parentScope})";

    public static readonly IReadOnlyList<UserTable> Tables =
    [
        // ── health ───────────────────────────────────────────────────────
        new("workout_sets", "saglik", ChildOf("session_id", "workout_sessions"), None),
        new("workout_sessions", "saglik", Owner, None),
        new("workout_plan_items", "saglik", ChildOf("plan_id", "workout_plans"), None),
        new("workout_plans", "saglik", Owner, None),
        new("meal_items", "saglik", ChildOf("meal_id", "meals"), None),
        new("meals", "saglik", Owner, None),
        new("medication_doses", "saglik", Owner, None),
        new("medication_times", "saglik", ChildOf("medication_id", "medications"), None),
        new("medications", "saglik", Owner, None),
        new("body_measurements", "saglik", Owner, None),
        new("health_goals", "saglik", Owner, None),

        // ── plan ─────────────────────────────────────────────────────────
        new("focus_sessions", "plan", Owner, None),
        new("schedule_entries", "plan", Owner, None),
        new("tasks", "plan", Owner, None),
        new("notes", "plan", Owner, None),
        new("events", "plan", Owner, None),
        new("project_changes", "plan", Owner, None),
        new("projects", "plan", Owner, None),
        new("courses", "plan", Owner, None),
        new("terms", "plan", Owner, None),

        // After everything that references them (RESTRICT keys above).
        new("exercises", "saglik", Owner, None),
        new("foods", "saglik", Owner, None),

        // ── account ──────────────────────────────────────────────────────
        new("notification_deliveries", "hesap", ChildOf("notification_id", "notifications", User), None),
        new("notifications", "hesap", User, None),
        new("notification_preferences", "hesap", User, None),
        new("devices", "hesap", User, ["fcm_token"]),
        new("mfa_recovery_codes", "hesap", User, ["code_hash"]),
        new("mfa_factors", "hesap", User, ["secret_encrypted"]),
        new("refresh_tokens", "hesap", User, ["token_hash"]),
        new("security_events", "hesap", User, None, Erase: false),

        // Identity's own tables: authenticator keys and external logins, not
        // anything a person would recognise as their data. Erased, not exported.
        new("user_tokens", "hesap", User, None, Export: false),
        new("user_logins", "hesap", User, None, Export: false),
        new("user_claims", "hesap", User, None, Export: false),
        new("user_roles", "hesap", User, None, Export: false),
    ];

    /// <summary>
    /// Tables that hold no one's personal data: shared catalogues and system
    /// plumbing. <c>users</c> itself is exported as profile.json and erased
    /// last, separately.
    /// </summary>
    public static readonly IReadOnlySet<string> NotUserData = new HashSet<string>
    {
        "users",
        "roles",
        "role_claims",
        // Carries only a notification id; a missing notification is marked done.
        "outbox_messages",
    };

    /// <summary>The profile, as a whitelist: never the password hash or stamps.</summary>
    public const string ProfileSql =
        """
        SELECT jsonb_pretty(jsonb_build_object(
            'id', u.id,
            'email', u.email,
            'emailConfirmed', u.email_confirmed,
            'displayName', u.display_name,
            'locale', u.locale,
            'timeZone', u.time_zone,
            'mfaRequired', u.mfa_required,
            'createdAt', u.created_at,
            'lastLoginAt', u.last_login_at,
            'passwordChangedAt', u.password_changed_at,
            'deletionScheduledAt', u.deletion_scheduled_at,
            'roles', COALESCE((
                SELECT jsonb_agg(r.name ORDER BY r.name)
                FROM user_roles ur JOIN roles r ON r.id = ur.role_id
                WHERE ur.user_id = u.id), '[]'::jsonb)))
        FROM users u
        WHERE u.id = @uid
        """;

    public static string ExportSql(UserTable t) =>
        $"""
         SELECT jsonb_pretty(COALESCE(jsonb_agg(to_jsonb(t) - @hidden::text[]), '[]'::jsonb))
         FROM {t.Table} t
         WHERE {t.Scope}
         """;

    public static string EraseSql(UserTable t) => $"DELETE FROM {t.Table} WHERE {t.Scope}";

    /// <summary>
    /// Security events outlive the account for six months (decided 29 Sep 2026):
    /// time, type, outcome and IP stay for abuse investigations; who it was
    /// goes. RetentionCleanup deletes them once erasedAt is six months old.
    /// </summary>
    public const string AnonymiseSecurityEventsSql =
        """
        UPDATE security_events
        SET user_id = NULL,
            user_agent = NULL,
            detail = jsonb_build_object('erasedAt', @now)
        WHERE user_id = @uid
        """;

    public const string EraseUserSql = "DELETE FROM users WHERE id = @uid";
}
