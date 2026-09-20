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

    // Configured ranges win; otherwise Cloudflare's published list. Development
    // has neither, which leaves the trust list EMPTY — ForwardedHeaders then
    // ignores the header entirely and the socket address is used, which is the
    // safe failure mode.
    var configured = builder.Configuration
        .GetSection("Cloudflare:TrustedNetworks").Get<string[]>() ?? [];

    var trusted = configured.Length > 0
        ? CloudflareRanges.Parse(configured)
        : builder.Environment.IsProduction()
            ? CloudflareRanges.Parse(CloudflareRanges.Published)
            : [];

    foreach (var network in trusted) options.KnownNetworks.Add(network);
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
app.MapMfaEndpoints();
app.MapAccountSecurityEndpoints();

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
