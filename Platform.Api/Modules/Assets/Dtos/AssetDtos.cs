using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Assets.Dtos;

public sealed record CreateAssetCategoryRequest
{
    public required string Name { get; init; }

    public string? Manufacturer { get; init; }

    public string? Description { get; init; }
}

public sealed record UpdateAssetCategoryRequest
{
    public required string Name { get; init; }

    public string? Manufacturer { get; init; }

    public string? Description { get; init; }
}

public sealed record AssetCategoryResponse(
    Guid Id,
    Guid TenantId,
    string Name,
    string? Manufacturer,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ScheduledDeletionAt,
    int LinkedAssetsCount);

public sealed record DeleteAssetCategoryResult(
    bool PermanentlyDeleted,
    int AffectedAssetsCount,
    AssetCategoryResponse? Category);

public sealed record CreateAssetRequest
{
    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required string Name { get; init; }

    public required string Tag { get; init; }

    public string? Location { get; init; }

    public string? SerialNumber { get; init; }

    public DateOnly? InstallationDate { get; init; }

    public AssetStatus Status { get; init; } = AssetStatus.Active;
}

public sealed record UpdateAssetRequest
{
    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required string Name { get; init; }

    public required string Tag { get; init; }

    public string? Location { get; init; }

    public string? SerialNumber { get; init; }

    public DateOnly? InstallationDate { get; init; }

    public required AssetStatus Status { get; init; }
}

public sealed record AssetResponse(
    Guid Id,
    Guid TenantId,
    Guid UnitId,
    Guid CategoryId,
    string Name,
    string Tag,
    string? Location,
    string? SerialNumber,
    DateOnly? InstallationDate,
    AssetStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ScheduledDeletionAt);

public sealed record DeleteAssetResult(
    bool PermanentlyDeleted,
    AssetResponse? Asset);

public sealed record BulkCreateAssetsRequest
{
    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required string BaseLocationName { get; init; }

    public required string BaseTag { get; init; }

    public required int StartNumber { get; init; }

    public required int EndNumber { get; init; }
}

public sealed record BulkCreateAssetsResponse(
    int CreatedCount,
    IReadOnlyList<AssetResponse> Assets);
