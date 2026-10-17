using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Modules.Users.Dtos;
using Platform.Api.Modules.Users.Services;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed class WorkOrderService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IHttpContextAccessor httpContextAccessor,
    IUserDirectoryService userDirectoryService,
    IPermissionResolver permissionResolver,
    IAssetRegistry assetRegistry) : IWorkOrderService
{
    public async Task<IReadOnlyList<WorkOrderResponse>> ListAsync(
        Guid? assetId,
        Guid? maintenancePlanId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        var query = dbContext.WorkOrders
            .AsNoTracking()
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(currentUser, cancellationToken))
        {
            query = query.Where(w => w.AssignedUserId == currentUser.Id);
        }

        if (assetId is Guid filteredAssetId)
        {
            query = query.Where(w => w.AssetId == filteredAssetId);
        }

        if (maintenancePlanId is Guid filteredPlanId)
        {
            query = query.Where(w => w.MaintenancePlanId == filteredPlanId);
        }

        var workOrders = await query
            .OrderByDescending(w => w.ScheduledDate)
            .ThenByDescending(w => w.CreatedAt)
            .ToListAsync(cancellationToken);

        return workOrders.Select(WorkOrderResponseMapper.ToResponse).ToList();
    }

    public async Task<WorkOrderResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        var query = dbContext.WorkOrders
            .AsNoTracking()
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(currentUser, cancellationToken))
        {
            query = query.Where(w => w.AssignedUserId == currentUser.Id);
        }

        var workOrder = await query
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        return workOrder is null ? null : WorkOrderResponseMapper.ToResponse(workOrder);
    }

    public async Task<WorkOrderResponse> CreateAsync(
        CreateWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var asset = await assetRegistry.RequireAssetAsync(request.AssetId, cancellationToken);

        MaintenancePlan? plan = null;
        if (request.MaintenancePlanId is Guid planId)
        {
            plan = await dbContext.MaintenancePlans
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
                ?? throw new KeyNotFoundException($"Maintenance plan '{planId}' was not found.");

            if (!plan.IsActive)
            {
                throw new ArgumentException("Maintenance plan is not active.");
            }

            if (!PmocAssetEligibility.Matches(asset, plan.TenantId, plan.UnitId, plan.AssetCategoryId))
            {
                throw new ArgumentException(
                    $"Asset '{asset.Id}' is not eligible for this maintenance plan.");
            }
        }

        var assignedUser = await WorkOrderAssigneeGuard.ResolveOptionalAsync(
            dbContext,
            permissionResolver,
            tenantId,
            request.AssignedUserId,
            cancellationToken);

        ValidateTasks(request.Tasks);

        async Task<WorkOrderResponse> InsertAsync()
        {
            if (plan is not null)
            {
                await PmocPlanAssetLock.AcquireAsync(
                    dbContext,
                    plan.Id,
                    asset.Id,
                    cancellationToken);

                var duplicate = await dbContext.WorkOrders.AnyAsync(
                    workOrder =>
                        workOrder.TenantId == plan.TenantId
                        && workOrder.AssetId == asset.Id
                        && workOrder.MaintenancePlanId == plan.Id
                        && workOrder.ScheduledDate == request.ScheduledDate
                        && workOrder.Status != WorkOrderStatus.Canceled,
                    cancellationToken);

                if (duplicate)
                {
                    throw new DuplicateWorkOrderException();
                }
            }

            var workOrder = new WorkOrder
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                MaintenancePlanId = plan?.Id,
                AssignedUserId = assignedUser?.Id,
                Status = WorkOrderStatus.Pending,
                ScheduledDate = request.ScheduledDate,
                Notes = NormalizeOptional(request.Notes),
                SourcePlanName = plan?.Name,
            };

            foreach (var taskDto in request.Tasks.OrderBy(t => t.Order))
            {
                workOrder.AddTask(new WorkOrderTask
                {
                    TenantId = tenantId,
                    WorkOrderId = workOrder.Id,
                    PlanTaskId = taskDto.PlanTaskId,
                    Title = taskDto.Title.Trim(),
                    InputType = taskDto.InputType,
                    Configuration = NormalizeConfiguration(taskDto.Configuration),
                    IsMandatory = taskDto.IsMandatory,
                    Order = taskDto.Order,
                    Value = null,
                });
            }

            dbContext.WorkOrders.Add(workOrder);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (
                plan is not null && WorkOrderGenerationService.IsPmocPeriodUniqueViolation(ex))
            {
                dbContext.ChangeTracker.Clear();
                throw new DuplicateWorkOrderException();
            }

            workOrder.Asset = asset;
            workOrder.AssignedUser = assignedUser;
            return WorkOrderResponseMapper.ToResponse(workOrder);
        }

        return await InTransactionAsync(InsertAsync, cancellationToken);
    }

    public async Task<WorkOrderResponse?> UpdateTaskValueAsync(
        Guid workOrderId,
        Guid taskId,
        UpdateWorkOrderTaskValueRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        var query = dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(currentUser, cancellationToken))
        {
            query = query.Where(w => w.AssignedUserId == currentUser.Id);
        }

        var workOrder = await query
            .FirstOrDefaultAsync(w => w.Id == workOrderId, cancellationToken);

        if (workOrder is null)
        {
            return null;
        }

        WorkOrderResponse? response = null;
        await InTransactionAsync(
            async () =>
            {
                await LockPlanAndWorkOrderAsync(workOrder, cancellationToken);
                await ReloadWorkOrderAsync(workOrder, cancellationToken);
                EnsureMutable(workOrder);

                var task = workOrder.Tasks.FirstOrDefault(t => t.Id == taskId);
                if (task is null)
                {
                    return;
                }

                task.Value = WorkOrderTaskValues.Normalize(task, request.Value);
                task.Touch();
                workOrder.Touch();
                await dbContext.SaveChangesAsync(cancellationToken);
                response = WorkOrderResponseMapper.ToResponse(workOrder);
            },
            cancellationToken);

        return response;
    }

    public async Task<WorkOrderResponse?> UpdateStatusAsync(
        Guid id,
        UpdateWorkOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        var query = dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(currentUser, cancellationToken))
        {
            query = query.Where(w => w.AssignedUserId == currentUser.Id);
        }

        var workOrder = await query
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (workOrder is null)
        {
            return null;
        }

        await InTransactionAsync(
            async () =>
            {
                await LockPlanAndWorkOrderAsync(workOrder, cancellationToken);
                await ReloadWorkOrderAsync(workOrder, cancellationToken);
                ApplyStatusChange(workOrder, request.Status);
                await dbContext.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);

        return WorkOrderResponseMapper.ToResponse(workOrder);
    }

    public async Task<WorkOrderResponse?> CancelAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        var workOrder = await FindMutableWorkOrderAsync(id, currentUser, cancellationToken);
        if (workOrder is null)
        {
            return null;
        }

        await InTransactionAsync(
            async () =>
            {
                await LockPlanAndWorkOrderAsync(workOrder, cancellationToken);
                await ReloadWorkOrderAsync(workOrder, cancellationToken);
                ApplyStatusChange(workOrder, WorkOrderStatus.Canceled);
                await dbContext.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);

        return WorkOrderResponseMapper.ToResponse(workOrder);
    }

    public async Task<WorkOrderResponse?> UpdateAssigneeAsync(
        Guid id,
        Guid? assignedUserId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();
        _ = await GetCurrentUserAsync(cancellationToken);

        var workOrder = await dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        if (workOrder is null)
        {
            return null;
        }

        var assignedUser = await WorkOrderAssigneeGuard.ResolveOptionalAsync(
            dbContext,
            permissionResolver,
            tenantId,
            assignedUserId,
            cancellationToken);

        await InTransactionAsync(
            async () =>
            {
                await WorkOrderRowLock.AcquireAsync(dbContext, tenantId, workOrder.Id, cancellationToken);
                await dbContext.Entry(workOrder).ReloadAsync(cancellationToken);
                workOrder.AssignedUserId = assignedUser?.Id;
                workOrder.AssignedUser = assignedUser;
                workOrder.Touch();
                await dbContext.SaveChangesAsync(cancellationToken);
            },
            cancellationToken);

        return WorkOrderResponseMapper.ToResponse(workOrder);
    }

    private static void ApplyStatusChange(WorkOrder workOrder, WorkOrderStatus nextStatus)
    {
        if (workOrder.Status == WorkOrderStatus.Canceled)
        {
            throw new WorkOrderClosedException();
        }

        if (workOrder.Status == WorkOrderStatus.Completed
            && nextStatus is WorkOrderStatus.Pending or WorkOrderStatus.InProgress)
        {
            throw new WorkOrderReopenNotAllowedException();
        }

        if (nextStatus == WorkOrderStatus.Completed)
        {
            var missing = workOrder.Tasks
                .Where(task => task.IsMandatory && !WorkOrderTaskValues.IsFilled(task))
                .OrderBy(task => task.Order)
                .ThenBy(task => task.Id)
                .Select(task => new IncompleteWorkOrderTask(task.Id, task.Title))
                .ToList();

            if (missing.Count > 0)
            {
                throw new MandatoryTasksIncompleteException(missing);
            }

            workOrder.CompletedDate = DateTimeOffset.UtcNow;
        }
        else if (nextStatus is WorkOrderStatus.Pending or WorkOrderStatus.InProgress or WorkOrderStatus.Canceled)
        {
            workOrder.CompletedDate = null;
        }

        workOrder.Status = nextStatus;
        workOrder.Touch();
    }

    private async Task<WorkOrder?> FindMutableWorkOrderAsync(
        Guid id,
        Platform.Api.Modules.Users.Dtos.CurrentUserResponse currentUser,
        CancellationToken cancellationToken)
    {
        var query = dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.AssignedUser)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (await RestrictToAssignedAsync(currentUser, cancellationToken))
        {
            query = query.Where(w => w.AssignedUserId == currentUser.Id);
        }

        return await query.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
    }

    private async Task LockPlanAndWorkOrderAsync(
        WorkOrder workOrder,
        CancellationToken cancellationToken)
    {
        if (workOrder.MaintenancePlanId is Guid planId)
        {
            await PmocPlanAssetLock.AcquireAsync(
                dbContext,
                planId,
                workOrder.AssetId,
                cancellationToken);
        }

        var tenantId = workOrder.TenantId;
        await WorkOrderRowLock.AcquireAsync(dbContext, tenantId, workOrder.Id, cancellationToken);
    }

    private async Task ReloadWorkOrderAsync(WorkOrder workOrder, CancellationToken cancellationToken)
    {
        await dbContext.Entry(workOrder).ReloadAsync(cancellationToken);
        foreach (var task in workOrder.Tasks.ToList())
        {
            await dbContext.Entry(task).ReloadAsync(cancellationToken);
        }
    }

    private static void EnsureMutable(WorkOrder workOrder)
    {
        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Canceled)
        {
            throw new WorkOrderClosedException();
        }
    }

    private Task InTransactionAsync(Func<Task> action, CancellationToken cancellationToken) =>
        InTransactionAsync(
            async () =>
            {
                await action();
                return 0;
            },
            cancellationToken);

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return await action();
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await action();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private async Task<bool> RestrictToAssignedAsync(
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

    private Task<CurrentUserResponse> GetCurrentUserAsync(
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor.HttpContext?.User
            ?? throw new UnauthorizedAccessException("Authenticated user context is required.");

        return userDirectoryService.GetCurrentAsync(principal, cancellationToken);
    }

    private static void ValidateTasks(IReadOnlyList<CreateWorkOrderTaskDto> tasks)
    {
        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one work order task is required.");
        }

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                throw new ArgumentException("Work order task title is required.");
            }

            NormalizeConfiguration(task.Configuration);
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string? NormalizeConfiguration(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        var trimmed = configuration.Trim();

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new ArgumentException("Work order task has invalid Configuration JSON.");
        }
    }
}
