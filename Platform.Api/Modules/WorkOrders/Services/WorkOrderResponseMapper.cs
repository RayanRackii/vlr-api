using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Modules.WorkOrders.Services;

internal static class WorkOrderResponseMapper
{
    public static WorkOrderResponse ToResponse(WorkOrder workOrder) =>
        new(
            workOrder.Id,
            workOrder.TenantId,
            workOrder.AssetId,
            workOrder.MaintenancePlanId,
            workOrder.SourcePlanName,
            workOrder.AssignedUserId,
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
            workOrder.AssignedUser is null
                ? null
                : new WorkOrderAssignedUserResponse(
                    workOrder.AssignedUser.Id,
                    workOrder.AssignedUser.FullName,
                    workOrder.AssignedUser.Email),
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
