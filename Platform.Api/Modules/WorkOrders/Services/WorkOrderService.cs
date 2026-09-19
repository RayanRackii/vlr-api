using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
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

        string? sourcePlanName = null;
        if (request.MaintenancePlanId is Guid planId)
        {
            var plan = await dbContext.MaintenancePlans
                .FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
                ?? throw new KeyNotFoundException($"Maintenance plan '{planId}' was not found.");

            sourcePlanName = plan.Name;
        }

        var assignedUser = await WorkOrderAssigneeGuard.ResolveOptionalAsync(
            dbContext,
            permissionResolver,
            tenantId,
            request.AssignedUserId,
            cancellationToken);

        ValidateTasks(request.Tasks);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var workOrder = new WorkOrder
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                MaintenancePlanId = request.MaintenancePlanId,
                AssignedUserId = assignedUser?.Id,
                Status = WorkOrderStatus.Pending,
                ScheduledDate = request.ScheduledDate,
                Notes = NormalizeOptional(request.Notes),
                SourcePlanName = sourcePlanName,
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
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            workOrder.Asset = asset;
            workOrder.AssignedUser = assignedUser;
            return WorkOrderResponseMapper.ToResponse(workOrder);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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

        if (workOrder.Status is WorkOrderStatus.Completed or WorkOrderStatus.Canceled)
        {
            throw new InvalidOperationException(
                $"Cannot update tasks on a work order with status '{workOrder.Status}'.");
        }

        var task = workOrder.Tasks.FirstOrDefault(t => t.Id == taskId);

        if (task is null)
        {
            return null;
        }

        task.Value = NormalizeOptional(request.Value);
        task.Touch();
        workOrder.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkOrderResponseMapper.ToResponse(workOrder);
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

        if (workOrder.Status == WorkOrderStatus.Canceled)
        {
            throw new InvalidOperationException("Cannot change status of a canceled work order.");
        }

        if (request.Status == WorkOrderStatus.Completed)
        {
            var missingMandatory = workOrder.Tasks
                .Where(t => t.IsMandatory && string.IsNullOrWhiteSpace(t.Value))
                .OrderBy(t => t.Order)
                .Select(t => t.Title)
                .ToList();

            if (missingMandatory.Count > 0)
            {
                throw new ArgumentException(
                    $"Mandatory tasks are incomplete: {string.Join(", ", missingMandatory)}.");
            }

            workOrder.CompletedDate = DateTimeOffset.UtcNow;
        }

        if (request.Status is WorkOrderStatus.Pending or WorkOrderStatus.InProgress)
        {
            workOrder.CompletedDate = null;
        }

        workOrder.Status = request.Status;
        workOrder.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return WorkOrderResponseMapper.ToResponse(workOrder);
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
