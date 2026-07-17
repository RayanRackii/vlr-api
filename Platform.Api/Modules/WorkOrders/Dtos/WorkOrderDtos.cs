using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.WorkOrders.Dtos;

public sealed record CreateWorkOrderTaskDto
{
    public Guid? PlanTaskId { get; init; }

    public required string Title { get; init; }

    public required TaskInputType InputType { get; init; }

    public bool IsMandatory { get; init; } = true;

    public required int Order { get; init; }

    public string? Configuration { get; init; }
}

public sealed record CreateWorkOrderRequest
{
    public required Guid AssetId { get; init; }

    public Guid? MaintenancePlanId { get; init; }

    public Guid? AssignedUserId { get; init; }

    public required DateOnly ScheduledDate { get; init; }

    public string? Notes { get; init; }

    public required List<CreateWorkOrderTaskDto> Tasks { get; init; }
}

public sealed record UpdateWorkOrderTaskValueRequest
{
    public string? Value { get; init; }
}

public sealed record UpdateWorkOrderStatusRequest
{
    public required WorkOrderStatus Status { get; init; }
}

public sealed record WorkOrderAssetResponse(
    Guid Id,
    Guid UnitId,
    Guid CategoryId,
    string Name,
    string Tag,
    string? Location,
    AssetStatus Status);

public sealed record WorkOrderTaskResponse(
    Guid Id,
    Guid TenantId,
    Guid WorkOrderId,
    Guid? PlanTaskId,
    string Title,
    TaskInputType InputType,
    string? Configuration,
    bool IsMandatory,
    int Order,
    string? Value,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record WorkOrderAssignedUserResponse(
    Guid Id,
    string FullName,
    string Email);

public sealed record WorkOrderResponse(
    Guid Id,
    Guid TenantId,
    Guid AssetId,
    Guid? MaintenancePlanId,
    Guid? AssignedUserId,
    WorkOrderStatus Status,
    DateOnly ScheduledDate,
    DateTimeOffset? CompletedDate,
    string? Notes,
    WorkOrderAssetResponse Asset,
    WorkOrderAssignedUserResponse? AssignedUser,
    IReadOnlyList<WorkOrderTaskResponse> Tasks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
