using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

internal static class WorkOrderAssigneeGuard
{
    public static async Task<User?> ResolveOptionalAsync(
        AppDbContext dbContext,
        IPermissionResolver permissionResolver,
        Guid tenantId,
        Guid? assignedUserId,
        CancellationToken cancellationToken)
    {
        if (assignedUserId is not Guid userId)
        {
            return null;
        }

        var assignedUser = await dbContext.Users
            .Include(user => user.UserRoles)
                .ThenInclude(userRole => userRole.Role)
            .FirstOrDefaultAsync(
                user => user.Id == userId && user.IsActive,
                cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Assigned user '{userId}' was not found.");

        var canExecute = await permissionResolver.HasPermissionAsync(
            tenantId,
            userId,
            Permissions.Os.WorkOrdersExecute,
            cancellationToken);

        if (!canExecute)
        {
            throw new ArgumentException(
                $"Assigned user '{userId}' cannot execute work orders.");
        }

        return assignedUser;
    }
}
