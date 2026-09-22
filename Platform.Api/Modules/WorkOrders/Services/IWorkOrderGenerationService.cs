using Platform.Api.Modules.WorkOrders.Dtos;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed record GenerateWorkOrderCommand(
    Guid PlanId,
    Guid AssetId,
    DateOnly ScheduledDate,
    Guid? AssignedUserId);

public enum PmocAutomaticGenerationOutcome
{
    Created,
    SkippedNotDue,
    SkippedOpenWorkOrder,
    SkippedRevalidation,
    SkippedDuplicate,
}

public sealed record PmocAutomaticGenerationResult(
    PmocAutomaticGenerationOutcome Outcome,
    Guid? WorkOrderId,
    DateOnly? ScheduledDate);

public interface IWorkOrderGenerationService
{
    Task<WorkOrderResponse> GenerateAsync(
        GenerateWorkOrderCommand command,
        CancellationToken cancellationToken);

    Task<PmocAutomaticGenerationResult> TryGenerateAutomaticAsync(
        Guid planId,
        Guid assetId,
        DateOnly asOfDate,
        CancellationToken cancellationToken);
}
