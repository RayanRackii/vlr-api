using Platform.Api.Modules.Customers.Services;
using Platform.Api.Modules.Users.Services;

namespace Platform.Api.Modules.Users;

public static class UsersModuleExtensions
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.AddScoped<IUserDirectoryService, UserDirectoryService>();
        services.AddScoped<ITenantCustomerDirectory, TenantCustomerDirectory>();
        return services;
    }
}
