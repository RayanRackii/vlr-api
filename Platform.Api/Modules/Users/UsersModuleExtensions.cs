using Platform.Api.Modules.Users.Services;

namespace Platform.Api.Modules.Users;

public static class UsersModuleExtensions
{
    public static IServiceCollection AddUsersModule(this IServiceCollection services)
    {
        services.AddScoped<IUserDirectoryService, UserDirectoryService>();
        return services;
    }
}
