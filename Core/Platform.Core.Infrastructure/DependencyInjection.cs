using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Core.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCorePersistence(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseNpgsql(
                connectionString,
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