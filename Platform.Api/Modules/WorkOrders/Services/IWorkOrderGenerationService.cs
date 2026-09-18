using Platform.Api.Modules.WorkOrders.Dtos;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed record GenerateWorkOrderCommand(
    Guid PlanId,
    Guid AssetId,
    DateOnly ScheduledDate,
    Guid? AssignedUserId);

public interface IWorkOrderGenerationService
{
    Task<WorkOrderResponse> GenerateAsync(
        GenerateWorkOrderCommand command,
        CancellationToken cancellationToken);
}
