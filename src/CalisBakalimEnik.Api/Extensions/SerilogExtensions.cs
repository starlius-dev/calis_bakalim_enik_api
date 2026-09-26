using Serilog;
using Serilog.Events;

namespace CalisBakalimEnik.Api.Extensions;

public static class SerilogExtensions
{
    public static void ConfigureSerilog(this IHostBuilder host)
    {
        host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "CalisBakalimEnik.Api")
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{CorrelationId}>{NewLine}{Exception}")
            .WriteTo.File("logs/enik-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Information));
    }

    /// <summary>
    /// How long a request may take before it is logged as a WARNING rather than
    /// as information.
    /// </summary>
    /// <remarks>
    /// Every request is already logged with its duration, which is useless for
    /// finding a slow one: the slow request looks exactly like the other ten
    /// thousand. Raising the level is what makes it findable — and what lets an
    /// alert exist at all, since alerting on "an Information line whose Elapsed
    /// property is large" is not something a log search does well.
    /// </remarks>
    private const int SlowRequestMs = 500;

    /// <summary>Request logging that carries the user and correlation id.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

            // A slow-request warning belongs here rather than in an endpoint
            // filter: this measures what the USER waited for, including
            // authentication, the denylist lookup and serialisation, and it
            // covers every route without a convention anyone has to remember.
            //
            // The first request after a deploy trips this — JIT and the EF
            // model build land on whoever arrives first. One warning per start
            // is the price; special-casing it would mean ignoring the one
            // request most likely to be genuinely slow.
            options.GetLevel = (http, elapsed, error) =>
            {
                if (error is not null || http.Response.StatusCode >= 500)
                    return LogEventLevel.Error;

                return elapsed > SlowRequestMs
                    ? LogEventLevel.Warning
                    : LogEventLevel.Information;
            };

            options.EnrichDiagnosticContext = (diagnostic, http) =>
            {
                diagnostic.Set("ClientIp", ClientIp(http));
                diagnostic.Set("UserAgent", http.Request.Headers.UserAgent.ToString());
                diagnostic.Set("ClientVersion", http.Request.Headers["X-Client-Version"].ToString());
                diagnostic.Set("ClientPlatform", http.Request.Headers["X-Client-Platform"].ToString());

                if (http.User.Identity?.IsAuthenticated == true)
                    diagnostic.Set("UserId", http.User.FindFirst("sub")?.Value ?? "unknown");
            };
        });
    }

    /// <summary>
    /// The vetted client address. Reading the header here as well would put an
    /// attacker-chosen string in the logs, which is worse than useless in the
    /// one place an incident is reconstructed from. See docs/SECURITY.md §1.
    /// </summary>
    private static string ClientIp(HttpContext http) => ClientAddress.OrUnknown(http);
}
