using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Api.Features.System;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Api.Services;
using CalisBakalimEnik.Application;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure;
using CalisBakalimEnik.Infrastructure.Persistence;
using Microsoft.AspNetCore.HttpOverrides;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.ConfigureSerilog();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApiVersioningSetup();
builder.Services.AddAuthSetup(builder.Configuration);

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

// Order is load-bearing: the denylist and current-user resolution both read a
// VALIDATED claim, so they must sit after authentication. Rate limiting joins in
// Phase 4. See docs/ARCHITECTURE.md §2.
app.UseAuthentication();
app.UseMiddleware<JwtDenylistMiddleware>();
app.UseAuthorization();

app.MapSystemEndpoints();
app.MapAuthEndpoints();

// Seed the global roles and their permission claims. Idempotent, and outside any
// migration so tightening a role in code actually tightens it in the database.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseSeeder>().SeedAsync();
}

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
