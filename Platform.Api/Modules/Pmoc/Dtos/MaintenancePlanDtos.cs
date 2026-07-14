using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Pmoc.Dtos;

public sealed record CreatePlanTaskDto
{
    public required string Title { get; init; }

    public required TaskInputType InputType { get; init; }

    public bool IsMandatory { get; init; } = true;

    public required int Order { get; init; }

    public string? Configuration { get; init; }
}

public sealed record CreateMaintenancePlanRequest
{
    public required Guid UnitId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required MaintenanceFrequency Frequency { get; init; }

    public required Guid AssetCategoryId { get; init; }

    public bool IsActive { get; init; } = true;

    public required List<CreatePlanTaskDto> Tasks { get; init; }
}

public sealed record UpdateMaintenancePlanRequest
{
    public required Guid UnitId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required MaintenanceFrequency Frequency { get; init; }

    public required Guid AssetCategoryId { get; init; }

    public required bool IsActive { get; init; }
}

public sealed record PlanTaskResponse(
    Guid Id,
    Guid TenantId,
    Guid MaintenancePlanId,
    string Title,
    TaskInputType InputType,
    bool IsMandatory,
    int Order,
    string? Configuration,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record MaintenancePlanResponse(
    Guid Id,
    Guid TenantId,
    Guid UnitId,
    string Name,
    string? Description,
    MaintenanceFrequency Frequency,
    Guid AssetCategoryId,
    bool IsActive,
    IReadOnlyList<PlanTaskResponse> Tasks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
