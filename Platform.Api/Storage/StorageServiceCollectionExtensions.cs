namespace Platform.Api.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
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

        return services;
    }
}
