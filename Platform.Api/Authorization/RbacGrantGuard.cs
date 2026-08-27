using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public interface IRbacGrantGuard
{
    Task EnsureCanGrantKeysAsync(
        RbacActor actor,
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken);

    Task EnsureCanAssignRolesAsync(
        RbacActor actor,
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken);

    Task EnsureLastAdminNotRemovedAsync(
        Guid tenantId,
        Guid targetUserId,
        IReadOnlyList<Role> nextRoles,
        CancellationToken cancellationToken);
}

public sealed class RbacGrantGuard(
    AppDbContext dbContext,
    IPermissionResolver permissionResolver,
    ILogger<RbacGrantGuard> logger) : IRbacGrantGuard
{
    public async Task EnsureCanGrantKeysAsync(
        RbacActor actor,
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        if (actor.IsPlatformAdminInTenant)
        {
            return;
        }

        if (actor.UserId is not Guid userId)
        {
            throw RbacException.PrivilegeEscalation();
        }

        var enabled = await permissionResolver.GetEnabledCatalogKeysAsync(
            actor.TenantId,
            cancellationToken);
        var actorPerms = await permissionResolver.GetEffectivePermissionsAsync(
            actor.TenantId,
            userId,
            cancellationToken);
        var actorIsAdmin = await ActorHasAdminWildcardAsync(
            actor.TenantId,
            userId,
            cancellationToken);

        foreach (var key in keys)
        {
            var allowed = enabled.Contains(key)
                ? actorPerms.Contains(key)
                : actorIsAdmin;

            if (!allowed)
            {
                logger.LogInformation(
                    "RBAC privilege-escalation rejected. TenantId={TenantId} ActorUserId={ActorUserId}",
                    actor.TenantId,
                    userId);
                throw RbacException.PrivilegeEscalation();
            }
        }
    }

    public async Task EnsureCanAssignRolesAsync(
        RbacActor actor,
        IReadOnlyList<Role> roles,
        CancellationToken cancellationToken)
    {
        if (roles.Any(role =>
                role.Name.Equals(SystemRoles.SuperAdmin, StringComparison.OrdinalIgnoreCase)))
        {
            throw RbacException.CannotAssignSuperAdmin();
        }

        var assignsAdmin = roles.Any(role =>
            role.IsSystemRole
            && role.Name.Equals(SystemRoles.Admin, StringComparison.OrdinalIgnoreCase));

        if (assignsAdmin && !actor.IsPlatformAdminInTenant)
        {
            if (actor.UserId is not Guid actorUserId
                || !await ActorHasAdminWildcardAsync(actor.TenantId, actorUserId, cancellationToken))
            {
                logger.LogInformation(
                    "RBAC privilege-escalation rejected. TenantId={TenantId} ActorUserId={ActorUserId}",
                    actor.TenantId,
                    actor.UserId);
                throw RbacException.PrivilegeEscalation();
            }
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var catalog = await permissionResolver.GetEnabledCatalogKeysAsync(
            actor.TenantId,
            cancellationToken);

        foreach (var role in roles)
        {
            if (role.IsSystemRole && PermissionResolver.IsAdminOrSuperAdminName(role.Name))
            {
                foreach (var key in catalog)
                {
                    keys.Add(key);
                }

                continue;
            }

            foreach (var rolePermission in role.RolePermissions)
            {
                if (catalog.Contains(rolePermission.Permission.Key))
                {
                    keys.Add(rolePermission.Permission.Key);
                }
            }
        }

        await EnsureCanGrantKeysAsync(actor, keys, cancellationToken);
    }

    public async Task EnsureLastAdminNotRemovedAsync(
        Guid tenantId,
        Guid targetUserId,
        IReadOnlyList<Role> nextRoles,
        CancellationToken cancellationToken)
    {
        var keepsAdmin = nextRoles.Any(role =>
            role.IsSystemRole && PermissionResolver.IsAdminOrSuperAdminName(role.Name));

        if (keepsAdmin)
        {
            return;
        }

        var adminUserIds = await dbContext.Users
            .Where(user =>
                user.TenantId == tenantId
                && user.IsActive
                && user.UserRoles.Any(userRole =>
                    userRole.Role.IsSystemRole
                    && (userRole.Role.Name.ToLower() == SystemRoles.Admin.ToLower()
                        || userRole.Role.Name.ToLower() == SystemRoles.SuperAdmin.ToLower())))
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        if (adminUserIds.Count == 1 && adminUserIds[0] == targetUserId)
        {
            logger.LogInformation(
                "RBAC last-admin rejected. TenantId={TenantId} UserId={UserId}",
                tenantId,
                targetUserId);
            throw RbacException.LastAdminProtected();
        }
    }

    private async Task<bool> ActorHasAdminWildcardAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Users
            .AsNoTracking()
            .AnyAsync(
                user => user.Id == userId
                    && user.TenantId == tenantId
                    && user.IsActive
                    && user.UserRoles.Any(userRole =>
                        userRole.Role.IsSystemRole
                        && (userRole.Role.Name.ToLower() == SystemRoles.Admin.ToLower()
                            || userRole.Role.Name.ToLower() == SystemRoles.SuperAdmin.ToLower())),
                cancellationToken);
    }
}
