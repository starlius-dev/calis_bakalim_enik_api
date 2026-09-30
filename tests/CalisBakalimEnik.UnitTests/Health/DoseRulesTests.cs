using CalisBakalimEnik.Api.Features.Health;
using CalisBakalimEnik.Domain.Health;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Health;

/// <summary>
/// Changing and undoing a dose's status (J70).
/// </summary>
public class DoseRulesTests
{
    private static readonly DateTimeOffset Nine = new(2026, 9, 30, 9, 0, 0, TimeSpan.Zero);

    private static MedicationDose Pending() => new() { ScheduledAt = Nine };

    [Fact]
    public void Taking_uses_one_tablet_and_records_the_tap_time()
    {
        var d = Pending();

        DoseRules.Set(d, DoseStatus.Taken, Nine.AddMinutes(5)).Should().Be(-1);

        d.Status.Should().Be(DoseStatus.Taken);
        d.TakenAt.Should().Be(Nine.AddMinutes(5));
        d.StatusChangedAt.Should().Be(Nine.AddMinutes(5));
    }

    [Fact]
    public void A_second_tap_on_the_same_state_changes_nothing()
    {
        var d = Pending();
        DoseRules.Set(d, DoseStatus.Taken, Nine);

        DoseRules.Set(d, DoseStatus.Taken, Nine.AddMinutes(1)).Should().Be(0);
        d.TakenAt.Should().Be(Nine);
    }

    [Fact]
    public void Taken_then_skipped_puts_the_tablet_back()
    {
        var d = Pending();
        DoseRules.Set(d, DoseStatus.Taken, Nine);

        DoseRules.Set(d, DoseStatus.Skipped, Nine.AddMinutes(2)).Should().Be(1);
        d.TakenAt.Should().BeNull();
    }

    [Fact]
    public void A_missed_dose_taken_late_is_recorded_at_the_tap()
    {
        var d = new MedicationDose
        {
            ScheduledAt = Nine,
            Status = DoseStatus.Missed,
            StatusChangedAt = Nine.AddHours(4),
        };

        DoseRules.Set(d, DoseStatus.Taken, Nine.AddHours(6)).Should().Be(-1);
        d.TakenAt.Should().Be(Nine.AddHours(6));
    }

    [Fact]
    public void A_change_stays_open_for_four_hours_after_it()
    {
        var d = Pending();
        var eleven = new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero);
        DoseRules.Set(d, DoseStatus.Taken, eleven);

        DoseRules.EditableUntil(d).Should().Be(eleven.AddHours(4), "23:00 locks at 03:00");
        DoseRules.IsLocked(d, eleven.AddHours(3).AddMinutes(59)).Should().BeFalse();
        DoseRules.IsLocked(d, eleven.AddHours(4)).Should().BeTrue();
    }

    [Fact]
    public void Every_change_restarts_the_four_hours()
    {
        var d = Pending();
        DoseRules.Set(d, DoseStatus.Taken, Nine);
        DoseRules.Set(d, DoseStatus.Skipped, Nine.AddHours(3));

        DoseRules.EditableUntil(d).Should().Be(Nine.AddHours(7));
    }

    [Fact]
    public void A_pending_dose_is_never_locked()
    {
        var d = Pending();
        DoseRules.EditableUntil(d).Should().BeNull();
        DoseRules.IsLocked(d, Nine.AddDays(3)).Should().BeFalse();
    }

    [Fact]
    public void Old_doses_without_a_stamp_lock_from_their_best_known_time()
    {
        var taken = new MedicationDose
        {
            ScheduledAt = Nine, Status = DoseStatus.Taken, TakenAt = Nine.AddMinutes(10),
        };
        DoseRules.EditableUntil(taken).Should().Be(Nine.AddMinutes(10).AddHours(4));

        var missed = new MedicationDose { ScheduledAt = Nine, Status = DoseStatus.Missed };
        DoseRules.EditableUntil(missed).Should().Be(Nine.AddHours(8), "missed at +4 h, then four more");
    }

    [Fact]
    public void Geri_al_is_pending_inside_the_grace_and_missed_after_it()
    {
        var d = Pending();
        DoseRules.Untouched(d, Nine.AddHours(1)).Should().Be(DoseStatus.Pending);
        DoseRules.Untouched(d, Nine.AddHours(4)).Should().Be(DoseStatus.Missed);
    }

    [Fact]
    public void Undo_restores_the_state_and_the_original_taken_time()
    {
        var d = Pending();
        DoseRules.Set(d, DoseStatus.Taken, Nine.AddMinutes(3));
        DoseRules.Set(d, DoseStatus.Skipped, Nine.AddMinutes(30));

        DoseRules.CanUndo(d, Nine.AddMinutes(31)).Should().BeTrue();
        DoseRules.Undo(d, Nine.AddMinutes(31)).Should().Be(-1);

        d.Status.Should().Be(DoseStatus.Taken);
        d.TakenAt.Should().Be(Nine.AddMinutes(3), "the tablet was taken then, not at the undo");
    }

    [Fact]
    public void Undo_goes_one_step_back_only()
    {
        var d = Pending();
        DoseRules.Set(d, DoseStatus.Taken, Nine);
        DoseRules.Undo(d, Nine.AddSeconds(4));

        d.Status.Should().Be(DoseStatus.Pending);
        DoseRules.CanUndo(d, Nine.AddSeconds(5)).Should().BeFalse();
        DoseRules.Undo(d, Nine.AddSeconds(5)).Should().Be(0);
    }

    [Fact]
    public void Nothing_to_undo_after_the_missed_sweep_or_once_locked()
    {
        var swept = new MedicationDose
        {
            ScheduledAt = Nine, Status = DoseStatus.Missed, StatusChangedAt = Nine.AddHours(4),
        };
        DoseRules.CanUndo(swept, Nine.AddHours(5)).Should().BeFalse();

        var d = Pending();
        DoseRules.Set(d, DoseStatus.Skipped, Nine);
        DoseRules.CanUndo(d, Nine.AddHours(4)).Should().BeFalse();
    }
}
