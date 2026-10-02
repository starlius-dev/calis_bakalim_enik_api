using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Health;

namespace CalisBakalimEnik.Api.Features.Health;

/// <summary>
/// Changing a dose's status after the fact (J70). Pure: every rule takes
/// "now" and is tested without a database or a clock.
/// </summary>
/// <remarks>
/// Alındı, atlandı and kaçırıldı used to be final the moment they were set,
/// so a wrong tap stayed wrong. Now a resolved dose can be changed, reset or
/// undone for four hours after its last change (decided 30 Sep 2026): a dose
/// ticked at 23:00 can still be fixed until 03:00. After that it is history.
/// A late "alındı" is recorded at the time of the tap.
/// </remarks>
public static class DoseRules
{
    /// <summary>How long a resolved dose stays changeable after its last change.</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromHours(4);

    /// <summary>
    /// When the last change happened. Doses resolved before this rule have no
    /// stamp; the best guess is when they were taken, or when they would have
    /// been swept as missed.
    /// </summary>
    public static DateTimeOffset ChangedAt(MedicationDose d) =>
        d.StatusChangedAt ?? d.TakenAt ?? d.ScheduledAt + MedicationDoseService.MissedGrace;

    /// <summary>Null while pending: an unresolved dose has no lock.</summary>
    public static DateTimeOffset? EditableUntil(MedicationDose d) =>
        d.Status == DoseStatus.Pending ? null : ChangedAt(d) + EditWindow;

    public static bool IsLocked(MedicationDose d, DateTimeOffset now) =>
        EditableUntil(d) is { } until && now >= until;

    public static bool CanUndo(MedicationDose d, DateTimeOffset now) =>
        d.PreviousStatus is not null && d.StatusChangedAt is not null && !IsLocked(d, now);

    /// <summary>
    /// What "Geri al" returns a dose to: pending while it is still inside the
    /// missed grace, missed after it, as if it had never been touched.
    /// </summary>
    public static DoseStatus Untouched(MedicationDose d, DateTimeOffset now) =>
        now < d.ScheduledAt + MedicationDoseService.MissedGrace
            ? DoseStatus.Pending
            : DoseStatus.Missed;

    /// <summary>
    /// Moves a dose to <paramref name="status"/>, remembering what it replaced.
    /// Returns the change to the stock count: -1 into taken, +1 out of it, as
    /// a skipped or missed tablet is still in the box. Setting the state it is
    /// already in changes nothing, so a second tap never takes a second tablet.
    /// </summary>
    public static int Set(MedicationDose d, DoseStatus status, DateTimeOffset now) =>
        Move(d, status, status == DoseStatus.Taken ? now : null, now, remember: true);

    /// <summary>
    /// Puts back what the last change replaced, including the original taken
    /// time. Returns the stock change; zero when there is nothing to undo.
    /// </summary>
    public static int Undo(MedicationDose d, DateTimeOffset now)
    {
        if (!CanUndo(d, now)) return 0;
        return Move(d, d.PreviousStatus!.Value, d.PreviousTakenAt, now, remember: false);
    }

    private static int Move(
        MedicationDose d,
        DoseStatus status,
        DateTimeOffset? takenAt,
        DateTimeOffset now,
        bool remember)
    {
        if (d.Status == status) return 0;

        var wasTaken = d.Status == DoseStatus.Taken;
        var isTaken = status == DoseStatus.Taken;

        if (remember)
        {
            d.PreviousStatus = d.Status;
            d.PreviousTakenAt = d.TakenAt;
        }
        else
        {
            // An undo is not itself undoable: the toast offers one step back.
            d.PreviousStatus = null;
            d.PreviousTakenAt = null;
        }

        d.Status = status;
        d.TakenAt = isTaken ? takenAt : null;
        d.StatusChangedAt = now;

        return wasTaken == isTaken ? 0 : isTaken ? -1 : 1;
    }
}
