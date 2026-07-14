using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed class WorkOrderService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IWorkOrderService
{
    public async Task<IReadOnlyList<WorkOrderResponse>> ListAsync(
        Guid? assetId,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var query = dbContext.WorkOrders
            .AsNoTracking()
            .Include(w => w.Asset)
            .Include(w => w.Tasks)
            .AsQueryable();

        if (assetId is Guid filteredAssetId)
        {
            query = query.Where(w => w.AssetId == filteredAssetId);
        }

        var workOrders = await query
            .OrderByDescending(w => w.ScheduledDate)
            .ThenByDescending(w => w.CreatedAt)
            .ToListAsync(cancellationToken);

        return workOrders.Select(ToResponse).ToList();
    }

    public async Task<WorkOrderResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var workOrder = await dbContext.WorkOrders
            .AsNoTracking()
            .Include(w => w.Asset)
            .Include(w => w.Tasks)
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

        return workOrder is null ? null : ToResponse(workOrder);
    }

    public async Task<WorkOrderResponse> CreateAsync(
        CreateWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var asset = await dbContext.Assets
            .FirstOrDefaultAsync(a => a.Id == request.AssetId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{request.AssetId}' was not found.");

        if (request.MaintenancePlanId is Guid planId)
        {
            var planExists = await dbContext.MaintenancePlans
                .AnyAsync(p => p.Id == planId, cancellationToken);

            if (!planExists)
            {
                throw new KeyNotFoundException($"Maintenance plan '{planId}' was not found.");
            }
        }

        ValidateTasks(request.Tasks);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var workOrder = new WorkOrder
            {
                TenantId = tenantId,
                AssetId = asset.Id,
                MaintenancePlanId = request.MaintenancePlanId,
                Status = WorkOrderStatus.Pending,
                ScheduledDate = request.ScheduledDate,
                Notes = NormalizeOptional(request.Notes),
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
            return ToResponse(workOrder);
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

        var workOrder = await dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.Tasks)
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

        return ToResponse(workOrder);
    }

    public async Task<WorkOrderResponse?> UpdateStatusAsync(
        Guid id,
        UpdateWorkOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var workOrder = await dbContext.WorkOrders
            .Include(w => w.Asset)
            .Include(w => w.Tasks)
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

        return ToResponse(workOrder);
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
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

    private static WorkOrderResponse ToResponse(WorkOrder workOrder) =>
        new(
            workOrder.Id,
            workOrder.TenantId,
            workOrder.AssetId,
            workOrder.MaintenancePlanId,
            workOrder.Status,
            workOrder.ScheduledDate,
            workOrder.CompletedDate,
            workOrder.Notes,
            new WorkOrderAssetResponse(
                workOrder.Asset.Id,
                workOrder.Asset.UnitId,
                workOrder.Asset.CategoryId,
                workOrder.Asset.Name,
                workOrder.Asset.Tag,
                workOrder.Asset.Location,
                workOrder.Asset.Status),
            workOrder.Tasks
                .OrderBy(t => t.Order)
                .Select(ToTaskResponse)
                .ToList(),
            workOrder.CreatedAt,
            workOrder.UpdatedAt);

    private static WorkOrderTaskResponse ToTaskResponse(WorkOrderTask task) =>
        new(
            task.Id,
            task.TenantId,
            task.WorkOrderId,
            task.PlanTaskId,
            task.Title,
            task.InputType,
            task.Configuration,
            task.IsMandatory,
            task.Order,
            task.Value,
            task.CreatedAt,
            task.UpdatedAt);
}
