using Platform.Api.Modules.RegistrationFields.Services;

namespace Platform.Api.Modules.RegistrationFields;

public static class RegistrationFieldsModuleExtensions
{
    public static IServiceCollection AddRegistrationFieldsModule(this IServiceCollection services)
    {
        services.AddScoped<IRegistrationFieldService, RegistrationFieldService>();
        return services;
    }
}
