using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Common;

internal static class DirectoryAuthorization
{
    public static ActionResult? RequireTenant(
        ControllerBase controller,
        ITenantProvider tenantProvider)
    {
        try
        {
            if (tenantProvider.TenantId is not Guid)
            {
                return controller.Unauthorized(new { error = "Tenant context is required." });
            }
        }
        catch (TenantResolutionException ex)
        {
            return controller.Unauthorized(new { error = ex.Message });
        }

        return null;
    }

    public static async Task<ActionResult?> GateAsync(
        ControllerBase controller,
        ITenantProvider tenantProvider,
        IAuthorizationService authorization,
        System.Security.Claims.ClaimsPrincipal user,
        string permissionKey)
    {
        var missingTenant = RequireTenant(controller, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        var allowed = await authorization.AuthorizeAsync(
            user,
            PermissionPolicies.Name(permissionKey));
        if (!allowed.Succeeded)
        {
            return controller.StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = RbacErrorCodes.Forbidden });
        }

        return null;
    }
}
