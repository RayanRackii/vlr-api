using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Platform.Api.Authentication;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

/// <summary>
/// Canonical commercial-module runtime gate. Complements RBAC; does not replace
/// <see cref="RequirePermissionAttribute"/>. Runs for <c>[AllowAnonymous]</c> actions.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireActiveModuleAttribute(string moduleKey) : Attribute, IAsyncAuthorizationFilter
{
    public const string InactiveModuleError = "Module is not active for this tenant.";

    public string ModuleKey { get; } = moduleKey;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var services = context.HttpContext.RequestServices;
        var tenantProvider = services.GetRequiredService<ITenantProvider>();

        if (tenantProvider.TenantId is null)
        {
            var subdomain = ResolvePublicSubdomain(context);
            if (!string.IsNullOrWhiteSpace(subdomain))
            {
                var binder = services.GetRequiredService<IPublicTenantBinder>();
                await binder.BindFromSubdomainAsync(subdomain, context.HttpContext.RequestAborted);
            }
        }

        if (tenantProvider.TenantId is null)
        {
            return;
        }

        var accessor = services.GetRequiredService<ITenantModuleAccessor>();
        var active = await accessor.GetActiveModuleKeysAsync(context.HttpContext.RequestAborted);
        var required = ModuleKey.Trim().ToLowerInvariant();

        if (active.Contains(required))
        {
            return;
        }

        context.Result = new ObjectResult(new { error = InactiveModuleError })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
    }

    private static string? ResolvePublicSubdomain(AuthorizationFilterContext context)
    {
        if (context.RouteData.Values.TryGetValue("subdomain", out var routeValue)
            && routeValue is string routeSubdomain
            && !string.IsNullOrWhiteSpace(routeSubdomain))
        {
            return routeSubdomain;
        }

        if (context.HttpContext.Request.Headers.TryGetValue(TenantHeaders.Subdomain, out var headerValues))
        {
            var header = headerValues.ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }
        }

        return null;
    }
}
