using Serilog;
using Serilog.Events;

namespace CalisBakalimEnik.Api.Extensions;

public static class SerilogExtensions
{
    public static void ConfigureSerilog(this IHostBuilder host)
    {
        host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Application", "CalisBakalimEnik.Api")
                .WriteTo.Console(outputTemplate:
                    "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} <{CorrelationId}>{NewLine}{Exception}");

            // A file sink only where one is configured, and nothing configures
            // one on the server.
            //
            // Under systemd the console sink IS the log: journald captures
            // stdout, rotates it, and serves it through `journalctl -u`. A
            // second copy written into the app directory would be wrong three
            // times over — the unit mounts the filesystem read-only apart from
            // the data directory, so opening it fails; the path was relative,
            // so it resolved against WorkingDirectory rather than anywhere
            // deliberate; and the deploy swap deletes files not present in the
            // new build, so every release would throw the history away.
            //
            // Development sets it, because there is no journald there.
            var path = context.Configuration["Serilog:FilePath"];

            if (!string.IsNullOrWhiteSpace(path))
            {
                configuration.WriteTo.File(path,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30,
                    restrictedToMinimumLevel: LogEventLevel.Information);
            }
        });
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
