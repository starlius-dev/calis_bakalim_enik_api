using CalisBakalimEnik.Application.Common.Interfaces;
using CalisBakalimEnik.Infrastructure.Identity;
using CalisBakalimEnik.Infrastructure.Persistence;
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

        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>("postgres", tags: ["ready"]);

        return services;
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
