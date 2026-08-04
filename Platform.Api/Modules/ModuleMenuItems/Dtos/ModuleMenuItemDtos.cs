namespace Platform.Api.Modules.ModuleMenuItems.Dtos;

public sealed record ModuleMenuItemDto(
    Guid Id,
    string ModuleName,
    string Label,
    int SortOrder,
    bool IsActive,
    Guid? RentalAssetId,
    Guid? AssetId);

public sealed record UpsertModuleMenuItemRequestDto
{
    public required string ModuleName { get; init; }

    public required string Label { get; init; }

    public int SortOrder { get; init; }

    public bool IsActive { get; init; } = true;

    public Guid? RentalAssetId { get; init; }
}

public sealed record UpdateModuleMenuItemRequestDto
{
    public required string Label { get; init; }

    public int SortOrder { get; init; }

    public bool IsActive { get; init; }

    public Guid? RentalAssetId { get; init; }
}
