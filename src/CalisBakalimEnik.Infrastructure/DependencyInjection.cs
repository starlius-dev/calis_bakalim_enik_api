using System.Net.Http.Headers;
using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Health;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Notifications;
using CalisBakalimEnik.Infrastructure.Persistence;
using CalisBakalimEnik.Infrastructure.Plan;
using CalisBakalimEnik.Infrastructure.Persistence.Interceptors;
using CalisBakalimEnik.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CalisBakalimEnik.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<AuditableEntityInterceptor>();

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            options.UseNpgsql(
                configuration.GetConnectionString("Default"),
                npgsql => npgsql.EnableRetryOnFailure(3));
            options.UseSnakeCaseNamingConvention();
            options.AddInterceptors(sp.GetRequiredService<AuditableEntityInterceptor>());
        });

        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        AddIdentity(services);
        AddRedis(services, configuration);

        services.AddSingleton<ITokenService, TokenService>();
        services.AddScoped<AuthService>();
        services.AddScoped<DatabaseSeeder>();
        AddEmail(services, configuration);

        // MFA (Phase 4)
        services.AddDataProtection();
        services.AddSingleton<TotpService>();
        services.AddSingleton<MfaChallengeStore>();
        services.AddSingleton<BruteForceGuard>();
        services.AddScoped<MfaService>();
        services.AddScoped<SecurityEventWriter>();

        AddNotifications(services, configuration);

        // Phase 6 — domain
        services.AddScoped<TaskReminders>();
        services.AddScoped<ReminderSync>();
        services.AddScoped<MedicationDoseService>();
        services.AddHostedService<MedicationDoseGenerator>();

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("postgres", tags: ["ready"]);

        return services;
    }

    /// <summary>Phase 5 — notifications, the outbox and its two loops.</summary>
    private static void AddNotifications(
        IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(PushOptions.SectionName);
        services.Configure<PushOptions>(section);

        var options = section.Get<PushOptions>() ?? new PushOptions();

        if (options.UsesFcm)
        {
            services.AddSingleton<IPushSender, FcmPushSender>();
        }
        else
        {
            // Unlike email, this is NOT refused in production: the app is
            // usable without push — the in-app inbox still works — and iOS push
            // is blocked on an Apple Developer membership anyway.
            services.AddSingleton<IPushSender, LoggingPushSender>();
        }

        services.AddScoped<NotificationService>();
        services.AddScoped<NotificationDispatcher>();

        services.AddHostedService<OutboxProcessor>();
        services.AddHostedService<ReminderScheduler>();
    }

    private static void AddEmail(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(EmailOptions.SectionName);
        services.Configure<EmailOptions>(section);

        var options = section.Get<EmailOptions>() ?? new EmailOptions();

        if (!options.UsesResend)
        {
            // Development only — Program.cs refuses to start elsewhere.
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
            return;
        }

        services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.ApiKey);

            // Signup blocks on this call. A minute of waiting on a hung TLS
            // handshake is worse for the user than a clear failure.
            client.Timeout = TimeSpan.FromSeconds(15);
        });
    }

    private static void AddIdentity(IServiceCollection services)
    {
        services.AddIdentityCore<AppUser>(options =>
            {
                // Mirrors the three live rules the design shows on the signup and
                // password screens. The client shows them; the server decides.
                options.Password.RequiredLength = 10;
                options.Password.RequireUppercase = true;
                options.Password.RequireDigit = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Password.RequireLowercase = false;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedEmail = true;

                // Lockout counters live in Redis, not here: Identity's
                // AccessFailedCount is a database write on every failed attempt,
                // which is a free write-amplification attack. See SECURITY.md §5.
                options.Lockout.AllowedForNewUsers = false;

                options.Tokens.AuthenticatorIssuer = "Calis Bakalim Enik";
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        // 210k iterations, raised from the 100k default. See docs/DATABASE.md §3.
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210_000);
    }

    private static void AddRedis(IServiceCollection services, IConfiguration configuration)
    {
        var connection = configuration["Redis:Configuration"];
        if (string.IsNullOrWhiteSpace(connection)) return;

        services.AddSingleton<IConnectionMultiplexer>(
            _ => ConnectionMultiplexer.Connect(connection));

        services.AddSingleton<ICacheStore, RedisCacheStore>();
    }
}
