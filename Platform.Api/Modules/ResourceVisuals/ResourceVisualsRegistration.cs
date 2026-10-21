using Microsoft.AspNetCore.Authorization;
using Platform.Api.Authentication;

namespace Platform.Api.Modules.ResourceVisuals;

/// <summary>Platform action names are policies, intentionally absent from tenant-grantable permissions.</summary>
public static class ResourceVisualPermissions
{
    public const string TemplatesRead = "platform.resource_templates.read";
    public const string TemplatesManage = "platform.resource_templates.manage";
    public const string TagsManage = "platform.resource_visual_tags.manage";
    public const string TenantSegmentsManage = "platform.tenant_segments.manage";
    public const string DemoMapsSeed = "platform.booking_maps.seed_demo";
    public const string SegmentsRead = "rentals.segments.read";
    public const string SegmentsWrite = "rentals.segments.write";
}
public static class ResourceVisualsRegistration
{
    public static IServiceCollection AddResourceVisuals(this IServiceCollection services)
    {
        services.AddScoped<ResourceVisualCatalogService>();
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options => options.Filters.Add<ResourceVisualExceptionFilter>());
        services.AddAuthorization(options =>
        {
            foreach (var key in new[] { ResourceVisualPermissions.TemplatesRead, ResourceVisualPermissions.TemplatesManage, ResourceVisualPermissions.TagsManage, ResourceVisualPermissions.TenantSegmentsManage, ResourceVisualPermissions.DemoMapsSeed })
                options.AddPolicy(key, new AuthorizationPolicyBuilder(SupabaseJwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireAssertion(context => !CustomerClaimTypes.IsCustomer(context.User)).AddRequirements(new PlatformAdminRequirement()).Build());
        });
        return services;
    }
}
