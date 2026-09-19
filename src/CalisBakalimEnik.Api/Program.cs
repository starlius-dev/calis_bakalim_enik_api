using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.System;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Api.Services;
using CalisBakalimEnik.Application;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ConfigureSerilog();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiVersioningSetup();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// Traffic arrives through a Cloudflare Tunnel. Trust ONLY Cloudflare: an empty
// KnownNetworks list means "trust everyone", which lets any caller spoof the client
// IP and reset another user's rate-limit bucket. See docs/SECURITY.md §1.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardedForHeaderName = "CF-Connecting-IP";
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var cidr in builder.Configuration.GetSection("Cloudflare:TrustedNetworks").Get<string[]>() ?? [])
    {
        var parts = cidr.Split('/');
        if (parts.Length == 2
            && System.Net.IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var length))
        {
            options.KnownNetworks.Add(new IPNetwork(prefix, length));
        }
    }
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Authentication, the JWT denylist, current-user resolution, rate limiting and
// authorization slot in here in Phases 3 and 4. Order is load-bearing: current-user
// resolution must sit AFTER authentication because it reads a validated claim.

app.MapSystemEndpoints();

try
{
    Log.Information("Starting Çalış Bakalım Enik API ({Environment})", app.Environment.EnvironmentName);
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "API terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can use WebApplicationFactory.</summary>
public partial class Program;
