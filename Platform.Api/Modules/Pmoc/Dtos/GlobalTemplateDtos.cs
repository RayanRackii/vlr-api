using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Pmoc.Dtos;

public sealed record GlobalTemplateTaskResponse(
    Guid Id,
    Guid GlobalMaintenanceTemplateId,
    string Title,
    TaskInputType InputType,
    string? Configuration,
    bool IsMandatory,
    int Order,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record GlobalMaintenanceTemplateResponse(
    Guid Id,
    string Name,
    string? Description,
    MaintenanceFrequency Frequency,
    string Jurisdiction,
    string TargetEquipmentType,
    IReadOnlyList<GlobalTemplateTaskResponse> Tasks,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
