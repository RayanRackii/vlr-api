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

        var wildcard = await permissionResolver.GetAdminWildcardKeysAsync(
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
            // Keys outside the wildcard view are not "inert": excluded keys such as
            // core.customers.read are live explicit grants. A tenant Admin may opt in
            // (they do not receive the key from the wildcard). Anyone else may hand
            // the key out only when they already hold it. Disabled-module and unknown
            // keys stay Admin-only.
            var allowed = wildcard.Contains(key)
                ? actorPerms.Contains(key)
                : actorIsAdmin
                    || (PermissionCatalog.AdminWildcardExcludedKeys.Contains(key)
                        && actorPerms.Contains(key));

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
        var wildcardCatalog = await permissionResolver.GetAdminWildcardKeysAsync(
            actor.TenantId,
            cancellationToken);

        foreach (var role in roles)
        {
            if (role.IsSystemRole && PermissionResolver.IsAdminOrSuperAdminName(role.Name))
            {
                foreach (var key in wildcardCatalog)
                {
                    keys.Add(key);
                }

                continue;
            }

            foreach (var rolePermission in role.RolePermissions)
            {
                var key = rolePermission.Permission.Key;
                if (IsHandedOutKey(key, wildcardCatalog))
                {
                    keys.Add(key);
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

    /// <summary>
    /// True when assigning <paramref name="key"/> would confer it.
    /// The wildcard catalog omits <see cref="PermissionCatalog.AdminWildcardExcludedKeys"/>
    /// and any explicit-grant key whose module is off. Those keys are still real
    /// grants and must reach <see cref="EnsureCanGrantKeysAsync"/>.
    /// </summary>
    private static bool IsHandedOutKey(string key, IReadOnlySet<string> wildcardCatalog) =>
        wildcardCatalog.Contains(key)
        || PermissionCatalog.AdminWildcardExcludedKeys.Contains(key)
        || PermissionCatalog.ExplicitGrantOnlyKeys.Contains(key);

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
