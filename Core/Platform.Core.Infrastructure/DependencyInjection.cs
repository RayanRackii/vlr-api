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
    /// <summary>EF pool budget — keep low for Supabase session pooler (pool_size ≈ 15).</summary>
    private const int EfMaxPoolSize = 5;

    public static IServiceCollection AddCorePersistence(
        this IServiceCollection services,
        string connectionString)
    {
        var bounded = NpgsqlConnectionStringHelper.WithBoundedPoolSize(
            connectionString,
            EfMaxPoolSize);

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

        return services;
    }

    public static IServiceCollection AddSupabaseAdminClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SupabaseOptions>(configuration.GetSection(SupabaseOptions.SectionName));

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
        });

        return services;
    }
}
