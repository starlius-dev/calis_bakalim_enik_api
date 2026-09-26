using System.Net;
using CalisBakalimEnik.Api.Extensions;
using CalisBakalimEnik.Api.Features.Admin;
using CalisBakalimEnik.Api.Features.Auth;
using CalisBakalimEnik.Api.Features.Content;
using CalisBakalimEnik.Api.Features.Health;
using CalisBakalimEnik.Api.Features.Notifications;
using CalisBakalimEnik.Api.Features.Plan;
using CalisBakalimEnik.Api.Features.System;
using CalisBakalimEnik.Api.Middleware;
using CalisBakalimEnik.Api.Services;
using CalisBakalimEnik.Application;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Services;
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

// The Flutter WEB build is a different origin from the API, so without this it
// cannot make a single call — the browser blocks the preflight.
//
// An explicit allowlist, never AllowAnyOrigin: the refresh token travels in a
// credentialed request, and browsers reject AllowAnyOrigin + AllowCredentials
// anyway. See docs/SECURITY.md §8.
const string corsPolicy = "app";

builder.Services.AddCors(options =>
{
    var origins = builder.Configuration
        .GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];

    options.AddPolicy(corsPolicy, policy =>
    {
        if (origins.Length == 0)
        {
            // No origins configured: allow nothing rather than everything.
            policy.WithOrigins();
            return;
        }

        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            // The client reads these, so they must be exposed explicitly.
            .WithExposedHeaders("X-Correlation-Id", "Retry-After", "X-Update-Available");
    });
});

// The client version floor. Absent by default, which leaves the gate inert —
// see ClientVersionMiddleware.
builder.Services.Configure<ClientOptions>(
    builder.Configuration.GetSection(ClientOptions.SectionName));

// Traffic arrives through a Cloudflare Tunnel. Trust ONLY Cloudflare: an empty
// KnownNetworks list means "trust everyone", which lets any caller spoof the client
// IP and reset another user's rate-limit bucket. See docs/SECURITY.md §1.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardedForHeaderName = "CF-Connecting-IP";
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();

    // Configured ranges win; otherwise Cloudflare's published list in
    // Production. Loopback is added to both, and the result is never empty —
    // see CloudflareRanges.TrustList for why that matters more than it looks.
    var configured = builder.Configuration
        .GetSection("Cloudflare:TrustedNetworks").Get<string[]>() ?? [];

    var trusted = CloudflareRanges.TrustList(configured, builder.Environment.IsProduction());

    foreach (var network in trusted) options.KnownNetworks.Add(network);
});

var app = builder.Build();

app.UseForwardedHeaders();

// Before CORS and everything else, so the headers are on a preflight and on a
// failure too — the responses most likely to be the ones an attacker sees.
app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseCors(corsPolicy);
app.UseMiddleware<CorrelationIdMiddleware>();

// Before authentication: a client that is too old should be told to update,
// not told its token is bad. Inert until Client:MinimumVersion is set.
app.UseMiddleware<ClientVersionMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else if (app.Services.GetRequiredService<IEmailSender>() is LoggingEmailSender)
{
    // Refuse to start rather than run a deployment where every confirmation
    // link is appended to a local file. The symptom otherwise is a healthy
    // service that nobody can finish signing up to.
    throw new InvalidOperationException(
        "Email:Provider must be Resend with an Email:ApiKey outside Development.");
}

if (!app.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(builder.Configuration["DataProtection:KeyPath"]))
{
    // Same reasoning, worse symptom. Without a persisted key ring the TOTP
    // secrets in the database cannot be decrypted after a redeploy, and the
    // service looks perfectly healthy while refusing every correct
    // authenticator code. Refusing to start is the kinder failure.
    throw new InvalidOperationException(
        "DataProtection:KeyPath must point at durable storage outside Development. "
        + "Without it every enrolled authenticator app breaks on the next deploy.");
}

// Order is load-bearing: the denylist and current-user resolution both read a
// VALIDATED claim, so they must sit after authentication. See
// docs/ARCHITECTURE.md §2.
app.UseAuthentication();
app.UseMiddleware<JwtDenylistMiddleware>();

// After authentication so the budget is partitioned by the real user, and
// before authorization so a flood of forbidden requests is still metered.
app.UseMiddleware<AuthenticatedRateLimitMiddleware>();

app.UseAuthorization();

// Everything is mapped through one group so the problem-details filter cannot
// be forgotten on a new feature. The prefix is empty — the endpoint files carry
// their own absolute paths — so this changes no route, only what every route
// runs. See docs/ARCHITECTURE.md §3.
var api = app.MapGroup("").CompleteProblemDetails();

api.MapSystemEndpoints();
api.MapAuthEndpoints();
api.MapMfaEndpoints();
api.MapAccountSecurityEndpoints();
api.MapNotificationEndpoints();
api.MapDeviceEndpoints();
api.MapTaskEndpoints();
api.MapPlanEndpoints();
api.MapContentEndpoints();
api.MapMedicationEndpoints();
api.MapWorkoutEndpoints();
api.MapNutritionEndpoints();
api.MapStatsEndpoints();
api.MapAdminEndpoints();

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
