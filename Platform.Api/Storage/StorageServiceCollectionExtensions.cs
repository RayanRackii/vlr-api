namespace Platform.Api.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorage(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(hostEnvironment);

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        var supabaseUrl = configuration["Storage:SupabaseUrl"];
        var serviceRoleKey = configuration["Storage:ServiceRoleKey"];
        var supabaseConfigured = !string.IsNullOrWhiteSpace(supabaseUrl)
            && !string.IsNullOrWhiteSpace(serviceRoleKey);

        if (supabaseConfigured)
        {
            services.AddHttpClient<IStorageProvider, SupabaseStorageProvider>();
        }
        else
        {
            services.AddSingleton<IStorageProvider, DevStorageProvider>();
        }

        services.AddHostedService(sp => new StorageProviderGateHostedService(
            usingDevStorage: !supabaseConfigured,
            isProduction: hostEnvironment.IsProduction(),
            sp.GetRequiredService<ILogger<StorageProviderGateHostedService>>()));

        return services;
    }
}

internal sealed class StorageProviderGateHostedService(
    bool usingDevStorage,
    bool isProduction,
    ILogger<StorageProviderGateHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (isProduction && usingDevStorage)
        {
            logger.LogError(
                "DevStorageProvider is selected in Production. Storage:SupabaseUrl or Storage:ServiceRoleKey is missing; catalog files will use local/dev URLs.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
