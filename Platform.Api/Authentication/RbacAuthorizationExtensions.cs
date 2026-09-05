using Microsoft.AspNetCore.Authorization;
using Platform.Api.Authorization;

namespace Platform.Api.Authentication;

public static class RbacAuthorizationExtensions
{
    public static IServiceCollection AddRbacAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, JsonForbiddenAuthorizationResultHandler>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<ITenantModuleAccessor, TenantModuleAccessor>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<ITenantAccessBootstrapper, TenantAccessBootstrapper>();
        services.AddScoped<IRbacActorAccessor, RbacActorAccessor>();
        services.AddScoped<IRbacGrantGuard, RbacGrantGuard>();
        return services;
    }
}
