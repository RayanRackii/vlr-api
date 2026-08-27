using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public interface ITenantAccessBootstrapper
{
    Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class TenantAccessBootstrapper(AppDbContext dbContext) : ITenantAccessBootstrapper
{
    public async Task EnsureAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await SeedMissingPermissionsAsync(cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await EnsureSystemRolesAsync(tenantId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SeedMissingPermissionsAsync(CancellationToken cancellationToken)
    {
        var existingKeys = await dbContext.Permissions
            .AsNoTracking()
            .Select(permission => permission.Key)
            .ToListAsync(cancellationToken);

        var existing = existingKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in PermissionCatalog.All)
        {
            if (existing.Contains(entry.Key))
            {
                continue;
            }

            dbContext.Permissions.Add(
                new Permission(entry.Key, entry.Name, entry.Description, entry.ModuleKey));
            existing.Add(entry.Key);
        }
    }

    private async Task EnsureSystemRolesAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var permissions = await dbContext.Permissions.ToListAsync(cancellationToken);
        var permissionsByKey = permissions.ToDictionary(
            permission => permission.Key,
            StringComparer.Ordinal);

        var roles = await dbContext.Roles
            .IgnoreQueryFilters()
            .Include(role => role.RolePermissions)
            .Where(role => role.TenantId == tenantId)
            .ToListAsync(cancellationToken);

        var admin = EnsureRole(roles, tenantId, SystemRoles.Admin, "Admin (system)");
        var user = EnsureRole(roles, tenantId, SystemRoles.User, "User (system)");
        var superAdmin = FindRole(roles, SystemRoles.SuperAdmin);
        var technician = FindRole(roles, SystemRoles.Technician);

        GrantKeys(admin, permissionsByKey, PermissionCatalog.AllKeys);
        GrantKeys(user, permissionsByKey, PermissionCatalog.DefaultUserKeys);

        if (superAdmin is not null)
        {
            GrantKeys(superAdmin, permissionsByKey, PermissionCatalog.AllKeys);
        }

        if (technician is not null)
        {
            GrantKeys(technician, permissionsByKey, PermissionCatalog.TechnicianLegacyKeys);
        }
    }

    private Role EnsureRole(
        List<Role> roles,
        Guid tenantId,
        string name,
        string description)
    {
        var existing = FindRole(roles, name);
        if (existing is not null)
        {
            return existing;
        }

        var role = new Role(tenantId, name, description, isSystemRole: true);
        dbContext.Roles.Add(role);
        roles.Add(role);
        return role;
    }

    private static Role? FindRole(IEnumerable<Role> roles, string name) =>
        roles.FirstOrDefault(role =>
            role.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private void GrantKeys(
        Role role,
        IReadOnlyDictionary<string, Permission> permissionsByKey,
        IReadOnlySet<string> keys)
    {
        var existingPermissionIds = role.RolePermissions
            .Select(rolePermission => rolePermission.PermissionId)
            .ToHashSet();

        foreach (var key in keys)
        {
            if (!permissionsByKey.TryGetValue(key, out var permission))
            {
                continue;
            }

            if (existingPermissionIds.Contains(permission.Id))
            {
                continue;
            }

            dbContext.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
            existingPermissionIds.Add(permission.Id);
        }
    }
}
