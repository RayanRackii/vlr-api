using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Modules.Assets;

public static class AssetsModuleExtensions
{
    public static IServiceCollection AddAssetsModule(this IServiceCollection services)
    {
        services.AddScoped<IAssetCategoryService, AssetCategoryService>();
        services.AddScoped<IAssetService, AssetService>();

        return services;
    }
}
