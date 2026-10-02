using CalisBakalimEnik.Domain.Health;
using CalisBakalimEnik.Infrastructure.Health;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Notifications;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Notifications;

/// <summary>
/// The two sources of notifications that were not worth sending (J65).
/// </summary>
public class NotificationNoiseTests
{
    // ── dose reminders ───────────────────────────────────────────────────

    [Fact]
    public void A_pending_dose_is_still_reminded()
    {
        ReminderScheduler.ShouldSendDoseReminder(DoseStatus.Pending, legacyMedication: false)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(DoseStatus.Taken)]
    [InlineData(DoseStatus.Skipped)]
    [InlineData(DoseStatus.Missed)]
    public void A_handled_dose_is_not(DoseStatus status)
    {
        ReminderScheduler.ShouldSendDoseReminder(status, legacyMedication: false)
            .Should().BeFalse();
    }

    [Fact]
    public void A_dose_that_no_longer_exists_is_not()
    {
        // Cleared by a schedule change or a pause.
        ReminderScheduler.ShouldSendDoseReminder(null, legacyMedication: false)
            .Should().BeFalse();
    }

    [Fact]
    public void An_old_reminder_carrying_the_medication_id_still_goes_out()
    {
        // Written before reminders carried the dose's id; which dose it was
        // cannot be told, so it behaves as it always did.
        ReminderScheduler.ShouldSendDoseReminder(null, legacyMedication: true)
            .Should().BeTrue();
    }

    [Fact]
    public void The_follow_up_comes_half_an_hour_after_the_dose()
    {
        MedicationDoseService.FollowUpAfter.Should().Be(TimeSpan.FromMinutes(30));
    }

    // ── new-device alert ─────────────────────────────────────────────────

    private static readonly HashSet<string> None = [];
    private const string Laptop = "a1";
    private const string Chrome129 = "Mozilla/5.0 Chrome/129";
    private const string Chrome130 = "Mozilla/5.0 Chrome/130";

    [Fact]
    public void A_browser_update_on_a_known_device_is_not_a_new_device()
    {
        AuthService.IsUnknownDevice(Laptop, Chrome130,
                knownInstallations: new HashSet<string> { Laptop },
                knownAgents: new HashSet<string> { Chrome129 },
                firstSignIn: false)
            .Should().BeFalse("the user-agent changed, the device did not");
    }

    [Fact]
    public void A_device_never_seen_before_is()
    {
        AuthService.IsUnknownDevice("b2", Chrome129,
                knownInstallations: new HashSet<string> { Laptop },
                knownAgents: new HashSet<string> { Chrome129 },
                firstSignIn: false)
            .Should().BeTrue("same browser text, different device");
    }

    [Fact]
    public void The_first_sign_in_of_an_account_never_alerts()
    {
        AuthService.IsUnknownDevice(Laptop, Chrome129, None, None, firstSignIn: true)
            .Should().BeFalse();
    }

    [Fact]
    public void While_no_session_has_an_id_the_user_agent_still_decides()
    {
        // The day this ships every earlier session has no installation id;
        // treating all of them as new would alert every account at once.
        AuthService.IsUnknownDevice(Laptop, Chrome129,
                None, new HashSet<string> { Chrome129 }, firstSignIn: false)
            .Should().BeFalse();

        AuthService.IsUnknownDevice(Laptop, "curl/8",
                None, new HashSet<string> { Chrome129 }, firstSignIn: false)
            .Should().BeTrue();
    }

    [Fact]
    public void An_old_client_without_an_id_is_judged_by_user_agent()
    {
        AuthService.IsUnknownDevice(null, Chrome129,
                new HashSet<string> { Laptop }, new HashSet<string> { Chrome129 },
                firstSignIn: false)
            .Should().BeFalse();
    }
}
