using CalisBakalimEnik.Domain.Plan;

namespace CalisBakalimEnik.Api.Features.Plan;

/// <summary>
/// The Odak timer, run on the server (J76). Pure: every rule takes "now" and
/// is tested without a database or a clock.
/// </summary>
/// <remarks>
/// Before this the timer lived in the page. Leaving Odak or reloading lost
/// it, and the server stored whatever the page last reported. Now the session
/// holds when it started, when it was paused and how long it has been paused,
/// and the focused time is worked out from those whenever it is asked for.
/// </remarks>
public static class FocusRules
{
    /// <summary>A timer left running stops itself at 24 hours of focus.</summary>
    public static readonly TimeSpan Cap = TimeSpan.FromHours(24);

    public static bool IsOpen(FocusSession s) => s.EndedAt is null;

    public static bool IsPaused(FocusSession s) => s.EndedAt is null && s.PausedAt is not null;

    /// <summary>Focused seconds so far: wall time minus paused time, capped.</summary>
    public static int ElapsedSeconds(FocusSession s, DateTimeOffset now)
    {
        var until = s.EndedAt ?? s.PausedAt ?? now;
        var seconds = (int)(until - s.StartedAt).TotalSeconds - s.PausedSeconds;
        return Math.Clamp(seconds, 0, (int)Cap.TotalSeconds);
    }

    /// <summary>
    /// Closes a running session that has reached the cap, at the moment it
    /// reached it. True when it did. A paused session never reaches it.
    /// </summary>
    public static bool ExpireIfOverCap(FocusSession s, DateTimeOffset now)
    {
        if (!IsOpen(s) || IsPaused(s)) return false;

        var capAt = s.StartedAt + TimeSpan.FromSeconds(s.PausedSeconds) + Cap;
        if (now < capAt) return false;

        s.EndedAt = capAt;
        Settle(s);
        return true;
    }

    public static void Pause(FocusSession s, DateTimeOffset now)
    {
        if (!IsOpen(s) || IsPaused(s)) return;
        s.PausedAt = now;
    }

    public static void Resume(FocusSession s, DateTimeOffset now)
    {
        if (!IsPaused(s)) return;
        s.PausedSeconds += Math.Max(0, (int)(now - s.PausedAt!.Value).TotalSeconds);
        s.PausedAt = null;
    }

    /// <summary>Stops it for good. Ending an ended session changes nothing.</summary>
    public static void End(FocusSession s, DateTimeOffset now)
    {
        if (!IsOpen(s)) return;

        // A paused session ends where it was paused: the pause is not focus.
        s.EndedAt = s.PausedAt ?? now;
        s.PausedAt = null;

        Settle(s);
    }

    /// <summary>
    /// Fixes the totals of an ended session: its focused time, and one block
    /// for any time at all (the plain-timer rule, one block per session).
    /// </summary>
    private static void Settle(FocusSession s)
    {
        var seconds = ElapsedSeconds(s, s.EndedAt!.Value);
        s.FocusSeconds = Math.Max(s.FocusSeconds, seconds);
        s.DoneBlocks = (short)Math.Max(s.DoneBlocks, s.FocusSeconds > 0 ? 1 : 0);
    }

    /// <summary>
    /// Where the block counter's "today" starts (J76): the start of the local
    /// day, unless a session still running began before it; then that
    /// session's day, so the counter does not reset under a running timer.
    /// </summary>
    public static DateOnly CounterDay(DateOnly today, DateOnly? openSessionStartedOn) =>
        openSessionStartedOn is { } started && started < today ? started : today;
}
