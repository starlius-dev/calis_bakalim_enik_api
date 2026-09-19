using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;

namespace CalisBakalimEnik.ArchitectureTests;

/// <summary>
/// The dependency rule is enforced, not trusted. Arrows point inward only:
/// Api -> Infrastructure -> Application -> Domain. See docs/ARCHITECTURE.md §1.
/// </summary>
public class DependencyRuleTests
{
    private static readonly Assembly Domain =
        typeof(Domain.Common.BaseEntity).Assembly;
    private static readonly Assembly Application =
        typeof(Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure =
        typeof(Infrastructure.DependencyInjection).Assembly;

    private const string DomainNs = "CalisBakalimEnik.Domain";
    private const string ApplicationNs = "CalisBakalimEnik.Application";
    private const string InfrastructureNs = "CalisBakalimEnik.Infrastructure";
    private const string ApiNs = "CalisBakalimEnik.Api";

    [Fact]
    public void Domain_should_not_depend_on_any_other_layer()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(ApplicationNs, InfrastructureNs, ApiNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain references nothing. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Domain_should_not_depend_on_infrastructure_concerns()
    {
        var result = Types.InAssembly(Domain)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Npgsql",
                "StackExchange.Redis",
                "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Domain must stay persistence-ignorant. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_should_not_depend_on_infrastructure_or_api()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny(InfrastructureNs, ApiNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Application declares ports; Infrastructure implements them. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_should_not_reference_a_database_provider()
    {
        var result = Types.InAssembly(Application)
            .ShouldNot()
            .HaveDependencyOnAny("Npgsql", "StackExchange.Redis")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Swapping Npgsql or Redis must touch Infrastructure only. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Infrastructure_should_not_depend_on_api()
    {
        var result = Types.InAssembly(Infrastructure)
            .ShouldNot()
            .HaveDependencyOn(ApiNs)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Api wires Infrastructure up, never the reverse. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}
