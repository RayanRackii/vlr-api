using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Modules.Assets;

public static class AssetsModuleExtensions
{
    public static IServiceCollection AddAssetsModule(this IServiceCollection services)
    {
        services.AddScoped<IAssetCategoryService, AssetCategoryService>();
        services.AddScoped<IAssetService, AssetService>();
        services.AddScoped<IAssetFamilyService, AssetFamilyService>();
        services.AddScoped<IAssetRegistry, AssetRegistry>();

        return services;
    }
}
