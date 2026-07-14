using Platform.Api.Modules.WorkOrders.Dtos;

namespace Platform.Api.Modules.WorkOrders.Services;

public interface IWorkOrderService
{
    Task<IReadOnlyList<WorkOrderResponse>> ListAsync(
        Guid? assetId,
        CancellationToken cancellationToken);

    Task<WorkOrderResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<WorkOrderResponse> CreateAsync(
        CreateWorkOrderRequest request,
        CancellationToken cancellationToken);

    Task<WorkOrderResponse?> UpdateTaskValueAsync(
        Guid workOrderId,
        Guid taskId,
        UpdateWorkOrderTaskValueRequest request,
        CancellationToken cancellationToken);

    Task<WorkOrderResponse?> UpdateStatusAsync(
        Guid id,
        UpdateWorkOrderStatusRequest request,
        CancellationToken cancellationToken);
}
