using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public sealed class PermissionAuthorizationHandler(
    IPermissionResolver permissionResolver,
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker,
    AppDbContext dbContext,
    ILogger<PermissionAuthorizationHandler> logger) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        try
        {
            if (platformAdminChecker.IsPlatformAdmin(context.User))
            {
                if (tenantProvider.TenantId is Guid)
                {
                    context.Succeed(requirement);
                }

                return;
            }

            if (tenantProvider.TenantId is not Guid tenantId)
            {
                return;
            }

            var supabaseAuthId = ResolveSupabaseAuthId(context.User);
            if (string.IsNullOrWhiteSpace(supabaseAuthId))
            {
                return;
            }

            var user = await dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(item =>
                    item.TenantId == tenantId
                    && item.SupabaseAuthId == supabaseAuthId
                    && item.IsActive);

            if (user is null)
            {
                return;
            }

            if (await permissionResolver.HasPermissionAsync(
                    tenantId,
                    user.Id,
                    requirement.PermissionKey))
            {
                context.Succeed(requirement);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "RBAC permission handler failed closed. Permission={Permission}",
                requirement.PermissionKey);
        }
    }

    private static string? ResolveSupabaseAuthId(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
