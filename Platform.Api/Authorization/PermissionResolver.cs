using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public sealed class PermissionResolver(
    AppDbContext dbContext,
    ITenantModuleAccessor tenantModuleAccessor,
    ILogger<PermissionResolver> logger) : IPermissionResolver
{
    private readonly Dictionary<(Guid TenantId, Guid UserId), IReadOnlySet<string>> _memo = [];

    public async Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetEffectivePermissionsCoreAsync(tenantId, userId, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "RBAC resolver failed closed. TenantId={TenantId} UserId={UserId}",
                tenantId,
                userId);
            return FrozenEmpty;
        }
    }

    public async Task<bool> HasPermissionAsync(
        Guid tenantId,
        Guid userId,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(permissionKey))
        {
            return false;
        }

        var effective = await GetEffectivePermissionsAsync(tenantId, userId, cancellationToken);
        return effective.Contains(permissionKey);
    }

    public async Task<IReadOnlySet<string>> GetEnabledCatalogKeysAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var activeModules = await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken);
            return FilterCatalog(activeModules);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "RBAC catalog filter failed closed. TenantId={TenantId}",
                tenantId);
            return FrozenEmpty;
        }
    }

    private async Task<IReadOnlySet<string>> GetEffectivePermissionsCoreAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var cacheKey = (tenantId, userId);
        if (_memo.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        if (tenantId == Guid.Empty || userId == Guid.Empty)
        {
            _memo[cacheKey] = FrozenEmpty;
            return FrozenEmpty;
        }

        var user = await dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(item => item.UserRoles)
                .ThenInclude(userRole => userRole.Role)
                    .ThenInclude(role => role.RolePermissions)
                        .ThenInclude(rolePermission => rolePermission.Permission)
            .FirstOrDefaultAsync(
                item => item.Id == userId && item.TenantId == tenantId,
                cancellationToken);

        if (user is null || !user.IsActive)
        {
            _memo[cacheKey] = FrozenEmpty;
            return FrozenEmpty;
        }

        var activeModules = await tenantModuleAccessor.GetActiveModuleKeysAsync(cancellationToken);

        IReadOnlySet<string> effective;
        if (HasAdminWildcard(user))
        {
            effective = FilterCatalog(activeModules);
        }
        else
        {
            var union = new HashSet<string>(StringComparer.Ordinal);
            foreach (var userRole in user.UserRoles)
            {
                foreach (var rolePermission in userRole.Role.RolePermissions)
                {
                    var permission = rolePermission.Permission;
                    if (IsModuleEnabled(permission.ModuleKey, activeModules))
                    {
                        union.Add(permission.Key);
                    }
                }
            }

            effective = union;
        }

        _memo[cacheKey] = effective;
        return effective;
    }

    private static IReadOnlySet<string> FilterCatalog(IReadOnlySet<string> activeModules)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in PermissionCatalog.All)
        {
            if (IsModuleEnabled(entry.ModuleKey, activeModules))
            {
                keys.Add(entry.Key);
            }
        }

        return keys;
    }

    private static bool IsModuleEnabled(string? moduleKey, IReadOnlySet<string> activeModules)
    {
        if (PermissionCatalog.IsCoreModule(moduleKey))
        {
            return true;
        }

        return activeModules.Contains(moduleKey!.Trim().ToLowerInvariant());
    }

    private static bool HasAdminWildcard(User user) =>
        user.UserRoles.Any(userRole =>
            userRole.Role.IsSystemRole
            && IsAdminOrSuperAdminName(userRole.Role.Name));

    internal static bool IsAdminOrSuperAdminName(string roleName) =>
        roleName.Equals(SystemRoles.Admin, StringComparison.OrdinalIgnoreCase)
        || roleName.Equals(SystemRoles.SuperAdmin, StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> FrozenEmpty =
        new HashSet<string>(StringComparer.Ordinal);
}
