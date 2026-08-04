using Platform.Api.Modules.ModuleMenuItems.Services;

namespace Platform.Api.Modules.ModuleMenuItems;

public static class ModuleMenuItemsModuleExtensions
{
    public static IServiceCollection AddModuleMenuItemsModule(this IServiceCollection services)
    {
        services.AddScoped<IModuleMenuItemService, ModuleMenuItemService>();
        return services;
    }
}
