using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

internal static class WorkOrderExecution
{
    public static Guid RequireTenant(ITenantProvider tenantProvider) =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    public static Task<CurrentUserResponse> GetCurrentUserAsync(
        IHttpContextAccessor httpContextAccessor,
        IUserDirectoryService userDirectoryService,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User
            ?? throw new UnauthorizedAccessException("Authenticated user context is required.");

        return userDirectoryService.GetCurrentAsync(principal, cancellationToken);
    }

    public static async Task<bool> RestrictToAssignedAsync(
        IPermissionResolver permissionResolver,
        ITenantProvider tenantProvider,
        CurrentUserResponse currentUser,
        CancellationToken cancellationToken)
    {
        if (currentUser.Id is not Guid userId || tenantProvider.TenantId is not Guid tenantId)
        {
            return false;
        }

        if (await permissionResolver.HasPermissionAsync(
                tenantId,
                userId,
                Permissions.Os.WorkOrdersCreate,
                cancellationToken))
        {
            return false;
        }

        if (currentUser.Roles.Any(role =>
                role.IsSystemRole && PermissionResolver.IsAdminOrSuperAdminName(role.Name)))
        {
            return false;
        }

        return await permissionResolver.HasPermissionAsync(
            tenantId,
            userId,
            Permissions.Os.WorkOrdersExecute,
            cancellationToken);
    }

    public static async Task<WorkOrder?> LockAndLoadAsync(
        AppDbContext dbContext,
        ITenantProvider tenantProvider,
        IHttpContextAccessor httpContextAccessor,
        IUserDirectoryService userDirectoryService,
        IPermissionResolver permissionResolver,
        Guid tenantId,
        Guid id,
        CancellationToken cancellationToken)
    {
        await WorkOrderRowLock.AcquireAsync(dbContext, tenantId, id, cancellationToken);
        var currentUser = await GetCurrentUserAsync(
            httpContextAccessor,
            userDirectoryService,
            cancellationToken);

        var query = dbContext.WorkOrders
            .Include(workOrder => workOrder.Asset)
            .Include(workOrder => workOrder.AssignedUser)
            .Include(workOrder => workOrder.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(
                permissionResolver,
                tenantProvider,
                currentUser,
                cancellationToken))
        {
            query = query.Where(workOrder => workOrder.AssignedUserId == currentUser.Id);
        }

        var workOrder = await query.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        if (workOrder.MaintenancePlanId is Guid planId)
        {
            await PmocPlanAssetLock.AcquireAsync(
                dbContext,
                planId,
                workOrder.AssetId,
                cancellationToken);
        }

        await ReloadAsync(dbContext, workOrder, cancellationToken);
        return workOrder;
    }

    public static async Task<WorkOrder?> LoadAccessibleAsync(
        AppDbContext dbContext,
        ITenantProvider tenantProvider,
        IHttpContextAccessor httpContextAccessor,
        IUserDirectoryService userDirectoryService,
        IPermissionResolver permissionResolver,
        Guid id,
        CancellationToken cancellationToken)
    {
        var currentUser = await GetCurrentUserAsync(
            httpContextAccessor,
            userDirectoryService,
            cancellationToken);

        var query = dbContext.WorkOrders
            .AsNoTracking()
            .Include(workOrder => workOrder.Asset)
            .Include(workOrder => workOrder.AssignedUser)
            .Include(workOrder => workOrder.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(
                permissionResolver,
                tenantProvider,
                currentUser,
                cancellationToken))
        {
            query = query.Where(workOrder => workOrder.AssignedUserId == currentUser.Id);
        }

        return await query.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public static void EnsureMutable(WorkOrder workOrder)
    {
        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Canceled)
        {
            throw new WorkOrderClosedException();
        }
    }

    private static async Task ReloadAsync(
        AppDbContext dbContext,
        WorkOrder workOrder,
        CancellationToken cancellationToken)
    {
        await dbContext.Entry(workOrder).ReloadAsync(cancellationToken);
        foreach (var task in workOrder.Tasks.ToList())
        {
            await dbContext.Entry(task).ReloadAsync(cancellationToken);
        }
    }
}
