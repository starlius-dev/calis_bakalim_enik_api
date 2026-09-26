using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CalisBakalimEnik.Api.Features.System;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // Version — the first question about any bug report is "which build".
        app.MapGet("/api/version", (
            IHostEnvironment env,
            Microsoft.Extensions.Options.IOptions<Auth.MfaOptions> mfa) =>
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? "0.0.0";

            var plus = informational.IndexOf('+');
            var version = plus > 0 ? informational[..plus] : informational;
            var metadata = plus > 0 ? informational[(plus + 1)..] : string.Empty;
            var parts = metadata.Split('.', 2);

            return Results.Ok(new
            {
                version,
                buildNumber = parts.Length > 0 ? parts[0] : "0",
                gitSha = parts.Length > 1 ? parts[1] : "local",
                apiVersions = new[] { "v1" },
                environment = env.EnvironmentName,
                // Reported so a client built with the wrong MFA flag is a
                // visible disagreement rather than a mystery: a UI that hides
                // the second factor while the server still demands one leaves
                // the user staring at a login that will not finish.
                mfaEnabled = mfa.Value.Enabled,
                builtAt = File.GetLastWriteTimeUtc(assembly.Location)
            });
        })
        .WithName("GetVersion")
        .WithTags("System");

        // Liveness must NEVER touch a dependency, or a database blip restarts a
        // healthy process. See docs/API-SURFACE.md §10.
        app.MapHealthChecks("/api/health/live", new HealthCheckOptions
        {
            Predicate = _ => false
        });

        app.MapHealthChecks("/api/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready")
        });

        return app;
    }
}
