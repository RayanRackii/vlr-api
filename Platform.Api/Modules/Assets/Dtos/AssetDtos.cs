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

    public required Guid FamilyId { get; init; }

    public required string Name { get; init; }

    public required string Tag { get; init; }

    public string? Location { get; init; }

    public string? SerialNumber { get; init; }

    public DateOnly? InstallationDate { get; init; }

    public AssetStatus Status { get; init; } = AssetStatus.Active;

    public bool IsRentable { get; init; }

    public bool RequiresMaintenance { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    /// <summary>
    /// When true (default), bookings wait for admin payment confirmation.
    /// </summary>
    public bool RequiresDeposit { get; init; } = true;

    /// <summary>
    /// Optional per-Location waiting queue. Ignored (and stored false) for Goods.
    /// </summary>
    public bool QueueEnabled { get; init; }

    /// <summary>Required when <see cref="QueueEnabled"/> is true on a Location.</summary>
    public TimeOnly? QueueOpeningTime { get; init; }

    public Dictionary<string, string?>? Attributes { get; init; }
}

public sealed record UpdateAssetRequest
{
    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required Guid FamilyId { get; init; }

    public required string Name { get; init; }

    public required string Tag { get; init; }

    public string? Location { get; init; }

    public string? SerialNumber { get; init; }

    public DateOnly? InstallationDate { get; init; }

    public required AssetStatus Status { get; init; }

    public bool IsRentable { get; init; }

    public bool RequiresMaintenance { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    /// <summary>
    /// When true (default), bookings wait for admin payment confirmation.
    /// </summary>
    public bool RequiresDeposit { get; init; } = true;

    /// <summary>
    /// Optional per-Location waiting queue. Ignored (and stored false) for Goods.
    /// </summary>
    public bool QueueEnabled { get; init; }

    /// <summary>Required when <see cref="QueueEnabled"/> is true on a Location.</summary>
    public TimeOnly? QueueOpeningTime { get; init; }

    public Dictionary<string, string?>? Attributes { get; init; }
}

public sealed record AssetRentalConfigResponse(
    Guid RentalAssetId,
    RentalAssetType Type,
    int TotalQuantity,
    bool IsActive,
    bool RequiresDeposit,
    bool QueueEnabled,
    TimeOnly? QueueOpeningTime);

public sealed record AssetResponse(
    Guid Id,
    Guid TenantId,
    Guid UnitId,
    Guid CategoryId,
    Guid FamilyId,
    string Name,
    string Tag,
    string? Location,
    string? SerialNumber,
    DateOnly? InstallationDate,
    AssetStatus Status,
    bool IsRentable,
    bool RequiresMaintenance,
    IReadOnlyDictionary<string, string?> Attributes,
    AssetRentalConfigResponse? RentalConfig,
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

    public required Guid FamilyId { get; init; }

    public required string BaseLocationName { get; init; }

    public required string BaseTag { get; init; }

    /// <summary>Required when <see cref="RentalType"/> is Location. Ignored for Good.</summary>
    public int? StartNumber { get; init; }

    /// <summary>Required when <see cref="RentalType"/> is Location. Ignored for Good.</summary>
    public int? EndNumber { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    public bool IsRentable { get; init; }

    public bool RequiresMaintenance { get; init; }

    /// <summary>
    /// When true (default), bookings wait for admin payment confirmation.
    /// </summary>
    public bool RequiresDeposit { get; init; } = true;

    /// <summary>
    /// Optional per-Location waiting queue. Ignored (and stored false) for Goods.
    /// </summary>
    public bool QueueEnabled { get; init; }

    /// <summary>Required when <see cref="QueueEnabled"/> is true on a Location.</summary>
    public TimeOnly? QueueOpeningTime { get; init; }

    public Dictionary<string, string?>? Attributes { get; init; }
}

public sealed record BulkCreateAssetsResponse(
    int CreatedCount,
    IReadOnlyList<AssetResponse> Assets);
