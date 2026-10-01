using CalisBakalimEnik.Api.Features.System;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Persistence.Configurations;
using FluentAssertions;

namespace CalisBakalimEnik.UnitTests.Api;

/// <summary>
/// Error reports from the app (D19): what is accepted, what is cut, how long
/// it is kept and that it leaves with the account.
/// </summary>
public class ClientErrorReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static ClientErrorRequest Request(
        string? kind = "flutter", string? message = "Null check operator used on a null value",
        string? stack = "#0 main (package:x/y.dart:1:1)") =>
        new(kind, message, stack, "/bugun", "01a0f88c-6098-7038-aa12-852b1bc45e37");

    [Fact]
    public void A_report_is_built_from_the_body_and_the_app_headers()
    {
        var user = Guid.NewGuid();
        var install = Guid.NewGuid();

        var report = ClientErrorEndpoints.Build(
            Request(), "WEB", "0.1.0+20", install.ToString(), "tr, en;q=0.8",
            "Mozilla/5.0", user, Now)!;

        report.Kind.Should().Be("flutter");
        report.Platform.Should().Be("web");
        report.AppVersion.Should().Be("0.1.0+20");
        report.InstallationId.Should().Be(install);
        report.UserId.Should().Be(user);
        report.Locale.Should().Be("tr");
        report.Route.Should().Be("/bugun");
        report.CorrelationId.Should().StartWith("01a0f88c");
        report.ReceivedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData(null, "boom")]
    [InlineData("crash", "boom")]
    [InlineData("flutter", null)]
    [InlineData("flutter", "   ")]
    public void Only_a_known_kind_with_a_message_is_a_report(string? kind, string? message)
    {
        ClientErrorEndpoints.Build(Request(kind, message), "web", "1", null, null, null, null, Now)
            .Should().BeNull();
    }

    [Fact]
    public void Over_long_fields_are_cut_to_the_columns_not_refused()
    {
        var report = ClientErrorEndpoints.Build(
            Request(message: new string('m', 5_000), stack: new string('s', 50_000)),
            "android", new string('v', 100), null, null, new string('u', 1_000), null, Now)!;

        report.Message.Should().HaveLength(ClientErrorReportConfiguration.MessageLength);
        report.Stack.Should().HaveLength(ClientErrorReportConfiguration.StackLength);
        report.AppVersion.Should().HaveLength(40);
        report.UserAgent.Should().HaveLength(400);
    }

    [Fact]
    public void Missing_or_odd_headers_still_give_a_usable_report()
    {
        var report = ClientErrorEndpoints.Build(
            Request(kind: "ASYNC"), "toaster", null, "not-a-guid", null, null, null, Now)!;

        report.Kind.Should().Be("async");
        report.Platform.Should().Be("unknown");
        report.AppVersion.Should().Be("unknown");
        report.InstallationId.Should().BeNull();
        report.UserId.Should().BeNull();
    }

    [Theory]
    [InlineData("one line", "one line")]
    [InlineData("first\nsecond\nthird", "first")]
    [InlineData("trailing   \nmore", "trailing")]
    public void The_list_shows_the_first_line(string message, string expected)
    {
        ClientErrorEndpoints.FirstLine(message).Should().Be(expected);
    }

    [Fact]
    public void Reports_are_kept_ninety_days_then_swept()
    {
        var rule = RetentionCleanup.Rules(new RetentionOptions())
            .Single(r => r.Table == "client_error_reports");

        rule.Days.Should().Be(90);
        rule.Predicate.Should().Be("received_at < @cutoff");
    }

    [Fact]
    public void Reports_are_exported_and_erased_with_the_account()
    {
        var table = UserDataMap.Tables.Single(t => t.Table == "client_error_reports");

        table.Export.Should().BeTrue();
        table.Erase.Should().BeTrue();
        table.Scope.Should().Be("user_id = @uid");
    }

    [Fact]
    public void Reporting_is_limited_per_address_because_it_works_before_sign_in()
    {
        var policy = RateLimitGuard.Policies.ClientErrors;

        policy.PerUser.Should().BeFalse();
        policy.Limit.Should().Be(30);
        policy.Window.Should().Be(TimeSpan.FromMinutes(10));
    }
}
