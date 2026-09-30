using CalisBakalimEnik.Api.Features.Account;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Changing the name and e-mail address behind a step-up, keeping the old
/// values for twelve months (J78).
/// </summary>
public class PersonalDetailsTests
{
    [Theory]
    [InlineData("ad@ornek.com", true)]
    [InlineData("zeynep.kaya+enik@ornek.com.tr", true)]
    [InlineData("ad@ornek", false)]
    [InlineData("Zeynep <ad@ornek.com>", false)]
    [InlineData("ad ornek.com", false)]
    [InlineData("", false)]
    public void Only_a_plain_address_is_accepted(string email, bool ok)
    {
        PersonalDetailsEndpoints.IsEmail(email).Should().Be(ok);
    }

    [Fact]
    public void The_name_rule_is_the_registration_rule()
    {
        PersonalDetailsEndpoints.CheckName("Z").Should().NotBeNull();
        PersonalDetailsEndpoints.CheckName("  Zeynep  ").Should().BeNull();
        PersonalDetailsEndpoints.CheckName(new string('a', 101)).Should().NotBeNull();
    }

    [Fact]
    public void Changing_details_is_its_own_step_up_purpose()
    {
        StepUpTokens.IsPurpose(StepUpTokens.Details).Should().BeTrue();
        StepUpTokens.Details.Should().NotBe(StepUpTokens.Export).And.NotBe(StepUpTokens.Delete);
    }

    [Fact]
    public void Old_values_are_kept_twelve_months_then_swept()
    {
        var rule = RetentionCleanup.Rules(new RetentionOptions())
            .Single(r => r.Table == "personal_detail_changes");

        rule.Days.Should().Be(365);
        rule.Predicate.Should().Be("changed_at < @cutoff");
    }

    [Fact]
    public void The_history_is_exported_and_erased_with_the_account()
    {
        var table = UserDataMap.Tables.Single(t => t.Table == "personal_detail_changes");

        table.Export.Should().BeTrue();
        table.Erase.Should().BeTrue();
        table.Scope.Should().Be("user_id = @uid");
    }

    [Fact]
    public void The_mails_carry_no_dash_characters()
    {
        var mails = new[]
        {
            PersonalDetailEmails.Confirm("Zeynep", "https://x/#/eposta-degistir"),
            PersonalDetailEmails.AlreadyUsed(),
            PersonalDetailEmails.Requested("Zeynep", "yeni@ornek.com"),
            PersonalDetailEmails.Changed("Zeynep", "yeni@ornek.com"),
        };

        mails.Should().AllSatisfy(m => m.Should().NotContainAny("—", "–"));
    }
}
