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

    /// <summary>Request logging that carries the user and correlation id.</summary>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate =
                "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";

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
    /// Traffic arrives through a Cloudflare Tunnel, so the socket address is the same for
    /// every user. The real client is in CF-Connecting-IP. See docs/SECURITY.md §1.
    /// </summary>
    private static string ClientIp(HttpContext http) =>
        http.Request.Headers.TryGetValue("CF-Connecting-IP", out var cf) && !string.IsNullOrWhiteSpace(cf)
            ? cf.ToString()
            : http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
