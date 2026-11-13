using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Core.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Default EF pool cap per API process. The Supabase session pooler admits 15 clients
    /// for the whole project, shared by every API machine (EF + Hangfire each) and ops tools:
    /// 2 machines x (4 EF + 3 Hangfire) = 14. Override with <c>Database:MaxPoolSize</c>
    /// when the machine count or the Supabase pool size changes.
    /// </summary>
    public const int DefaultEfMaxPoolSize = 4;

    public static IServiceCollection AddCorePersistence(
        this IServiceCollection services,
        string connectionString,
        int? maxPoolSize = null)
    {
        var cap = maxPoolSize ?? DefaultEfMaxPoolSize;
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1, nameof(maxPoolSize));
        var bounded = NpgsqlConnectionStringHelper.WithBoundedPoolSize(
            connectionString,
            cap);

        // Dictionary/POCO → jsonb requires explicit opt-in (Npgsql 8+).
        // Build the data source ONCE (singleton) — a new builder per request leaks pool slots.
        var dataSource = new NpgsqlDataSourceBuilder(bounded)
            .EnableDynamicJson()
            .Build();

        services.AddSingleton(dataSource);

        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                dataSource,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", "core"))
            .UseSnakeCaseNamingConvention());

        services.AddScoped<AmbientTenantContext>();
        services.AddScoped<ITenantContext, TenantContext>();

        return services;
    }

    public static IServiceCollection AddSupabaseAdminClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SupabaseOptions>(configuration.GetSection(SupabaseOptions.SectionName));
        services.AddSingleton<SupabaseUserTenantMetadataCache>();

        services.AddHttpClient<ISupabaseAuthAdminClient, SupabaseAuthAdminClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<SupabaseOptions>>().Value;

            if (string.IsNullOrWhiteSpace(options.Url))
            {
                throw new InvalidOperationException("Supabase:Url is not configured.");
            }

            if (string.IsNullOrWhiteSpace(options.ServiceRoleKey))
            {
                throw new InvalidOperationException("Supabase:ServiceRoleKey is not configured.");
            }

            client.BaseAddress = new Uri($"{options.Url.TrimEnd('/')}/auth/v1/");
            client.Timeout = SupabaseAuthAdminClient.RequestTimeout;
        });

        return services;
    }
}
