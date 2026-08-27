using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Roles.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Roles.Services;

public sealed class RoleService(
    AppDbContext dbContext,
    IRbacGrantGuard grantGuard,
    ILogger<RoleService> logger) : IRoleService
{
    public async Task<IReadOnlyList<RoleResponse>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var roles = await dbContext.Roles
            .AsNoTracking()
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
            .Where(role => role.TenantId == tenantId
                && role.Name.ToLower() != SystemRoles.SuperAdmin.ToLower())
            .OrderBy(role => role.Name)
            .ToListAsync(cancellationToken);

        return roles.Select(ToResponse).ToList();
    }

    public async Task<RoleResponse?> GetByIdAsync(
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        var role = await LoadRoleAsync(tenantId, roleId, cancellationToken);
        return role is null ? null : ToResponse(role);
    }

    public async Task<RoleResponse> CreateAsync(
        Guid tenantId,
        RbacActor actor,
        CreateRoleRequest request,
        CancellationToken cancellationToken)
    {
        var name = NormalizeName(request.Name);
        EnsureNotReservedName(name);
        await EnsureNameIsUniqueAsync(tenantId, name, excludeRoleId: null, cancellationToken);

        var keys = NormalizeKeys(request.PermissionKeys);
        await grantGuard.EnsureCanGrantKeysAsync(actor, keys, cancellationToken);

        var role = new Role(tenantId, name, NormalizeDescription(request.Description), isSystemRole: false);
        dbContext.Roles.Add(role);
        await ReplaceRolePermissionRowsAsync(role, keys, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC role created. TenantId={TenantId} RoleId={RoleId} Name={Name}",
            tenantId,
            role.Id,
            role.Name);

        return ToResponse(await ReloadAsync(role.Id, cancellationToken));
    }

    public async Task<RoleResponse> PatchAsync(
        Guid tenantId,
        RbacActor actor,
        Guid roleId,
        PatchRoleRequest request,
        CancellationToken cancellationToken)
    {
        var role = await LoadRoleAsync(tenantId, roleId, cancellationToken)
            ?? throw new KeyNotFoundException("Role was not found.");

        if (role.IsSystemRole && (request.Name is not null || request.Description is not null))
        {
            throw RbacException.CannotModifySystemRole();
        }

        if (request.Name is not null)
        {
            var name = NormalizeName(request.Name);
            EnsureNotReservedName(name);
            await EnsureNameIsUniqueAsync(tenantId, name, role.Id, cancellationToken);
            role.UpdateProfile(name, request.Description is null
                ? role.Description
                : NormalizeDescription(request.Description));
        }
        else if (request.Description is not null)
        {
            role.UpdateProfile(role.Name, NormalizeDescription(request.Description));
        }

        if (request.PermissionKeys is not null)
        {
            await ReplacePermissionsCoreAsync(actor, role, request.PermissionKeys, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC role updated. TenantId={TenantId} RoleId={RoleId} Name={Name}",
            tenantId,
            role.Id,
            role.Name);

        return ToResponse(await ReloadAsync(role.Id, cancellationToken));
    }

    public async Task<RoleResponse> ReplacePermissionsAsync(
        Guid tenantId,
        RbacActor actor,
        Guid roleId,
        IReadOnlyList<string> permissionKeys,
        CancellationToken cancellationToken)
    {
        var role = await LoadRoleAsync(tenantId, roleId, cancellationToken)
            ?? throw new KeyNotFoundException("Role was not found.");

        await ReplacePermissionsCoreAsync(actor, role, permissionKeys, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC role permissions changed. TenantId={TenantId} RoleId={RoleId} Name={Name}",
            tenantId,
            role.Id,
            role.Name);

        return ToResponse(await ReloadAsync(role.Id, cancellationToken));
    }

    public async Task DeleteAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken)
    {
        var role = await dbContext.Roles
            .Include(item => item.UserRoles)
            .FirstOrDefaultAsync(item => item.Id == roleId && item.TenantId == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Role was not found.");

        if (role.IsSystemRole)
        {
            throw RbacException.CannotDeleteSystemRole();
        }

        if (role.UserRoles.Count > 0)
        {
            throw RbacException.RoleInUse();
        }

        dbContext.Roles.Remove(role);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "RBAC role deleted. TenantId={TenantId} RoleId={RoleId} Name={Name}",
            tenantId,
            role.Id,
            role.Name);
    }

    private async Task ReplacePermissionsCoreAsync(
        RbacActor actor,
        Role role,
        IReadOnlyList<string> permissionKeys,
        CancellationToken cancellationToken)
    {
        if (role.IsSystemRole
            && PermissionResolver.IsAdminOrSuperAdminName(role.Name))
        {
            throw RbacException.CannotModifySystemRole();
        }

        var keys = NormalizeKeys(permissionKeys);
        await grantGuard.EnsureCanGrantKeysAsync(actor, keys, cancellationToken);
        await ReplaceRolePermissionRowsAsync(role, keys, cancellationToken);
    }

    private async Task ReplaceRolePermissionRowsAsync(
        Role role,
        IReadOnlySet<string> keys,
        CancellationToken cancellationToken)
    {
        var permissions = await dbContext.Permissions
            .Where(permission => keys.Contains(permission.Key))
            .ToListAsync(cancellationToken);

        var existing = await dbContext.RolePermissions
            .Where(rolePermission => rolePermission.RoleId == role.Id)
            .ToListAsync(cancellationToken);

        dbContext.RolePermissions.RemoveRange(existing);
        foreach (var permission in permissions)
        {
            dbContext.RolePermissions.Add(new RolePermission(role.Id, permission.Id));
        }
    }

    private async Task EnsureNameIsUniqueAsync(
        Guid tenantId,
        string name,
        Guid? excludeRoleId,
        CancellationToken cancellationToken)
    {
        var exists = await dbContext.Roles
            .AnyAsync(
                role => role.TenantId == tenantId
                    && role.Name.ToLower() == name.ToLower()
                    && (excludeRoleId == null || role.Id != excludeRoleId),
                cancellationToken);

        if (exists)
        {
            throw new ArgumentException("A role with this name already exists.");
        }
    }

    private async Task<Role?> LoadRoleAsync(
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken)
    {
        return await dbContext.Roles
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
            .FirstOrDefaultAsync(
                role => role.Id == roleId && role.TenantId == tenantId,
                cancellationToken);
    }

    private async Task<Role> ReloadAsync(Guid roleId, CancellationToken cancellationToken)
    {
        return await dbContext.Roles
            .AsNoTracking()
            .Include(role => role.RolePermissions)
                .ThenInclude(rolePermission => rolePermission.Permission)
            .FirstAsync(role => role.Id == roleId, cancellationToken);
    }

    private static IReadOnlySet<string> NormalizeKeys(IReadOnlyList<string>? keys)
    {
        var normalized = (keys ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (normalized.Any(key => !PermissionCatalog.AllKeys.Contains(key)))
        {
            throw new ArgumentException("One or more permission keys are not in the catalog.");
        }

        return normalized;
    }

    private static void EnsureNotReservedName(string name)
    {
        if (name.Equals(SystemRoles.SuperAdmin, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SystemRoles.Admin, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SystemRoles.User, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SystemRoles.Technician, StringComparison.OrdinalIgnoreCase)
            || name.Equals(SystemRoles.Client, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("This role name is reserved.");
        }
    }

    private static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length < 2)
        {
            throw new ArgumentException("Name is required.");
        }

        return trimmed;
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        return description.Trim();
    }

    private static RoleResponse ToResponse(Role role) =>
        new(
            role.Id,
            role.Name,
            role.Description,
            role.IsSystemRole,
            role.RolePermissions
                .Select(rolePermission => rolePermission.Permission.Key)
                .OrderBy(key => key)
                .ToList());
}
