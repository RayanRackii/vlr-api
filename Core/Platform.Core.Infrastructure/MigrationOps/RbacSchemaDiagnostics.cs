using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class RbacSchemaDiagnostics
{
    public static async Task<RbacSchemaDiagnosticCounts> CollectAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var clientAssignments = await CountActiveAssignmentsAsync(
            dbContext,
            SystemRoles.Client,
            cancellationToken);
        var superAdminAssignments = await CountActiveAssignmentsAsync(
            dbContext,
            SystemRoles.SuperAdmin,
            cancellationToken);
        var technicianAssignments = await CountActiveAssignmentsAsync(
            dbContext,
            SystemRoles.Technician,
            cancellationToken);

        var duplicateRoleNameGroups = await dbContext.Roles
            .IgnoreQueryFilters()
            .GroupBy(role => new { role.TenantId, Name = role.Name.ToLower() })
            .Where(group => group.Count() > 1)
            .CountAsync(cancellationToken);

        var userIds = dbContext.Users.IgnoreQueryFilters().Select(user => user.Id);
        var roleIds = dbContext.Roles.IgnoreQueryFilters().Select(role => role.Id);
        var permissionIds = dbContext.Permissions.IgnoreQueryFilters().Select(permission => permission.Id);

        var orphanUserRoles = await dbContext.UserRoles
            .IgnoreQueryFilters()
            .CountAsync(
                userRole => !userIds.Contains(userRole.UserId) || !roleIds.Contains(userRole.RoleId),
                cancellationToken);

        var orphanRolePermissions = await dbContext.RolePermissions
            .IgnoreQueryFilters()
            .CountAsync(
                rolePermission =>
                    !roleIds.Contains(rolePermission.RoleId)
                    || !permissionIds.Contains(rolePermission.PermissionId),
                cancellationToken);

        return new RbacSchemaDiagnosticCounts(
            clientAssignments,
            superAdminAssignments,
            technicianAssignments,
            duplicateRoleNameGroups,
            orphanUserRoles,
            orphanRolePermissions);
    }

    private static Task<int> CountActiveAssignmentsAsync(
        AppDbContext dbContext,
        string roleName,
        CancellationToken cancellationToken)
    {
        var expected = roleName.ToLower();
        return dbContext.UserRoles
            .IgnoreQueryFilters()
            .Where(userRole =>
                userRole.Role.Name.ToLower() == expected
                && userRole.User.IsActive)
            .CountAsync(cancellationToken);
    }
}
