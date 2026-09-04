using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Assets.Dtos;

public sealed record RegistryAssetListItem(
    Guid Id,
    string Name,
    string Tag,
    Guid UnitId,
    Guid CategoryId,
    AssetStatus Status);

public sealed record RegistryCategoryListItem(
    Guid Id,
    string Name);

public sealed record CreateRentableRequest
{
    public required string Name { get; init; }

    public required string Tag { get; init; }

    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required Guid FamilyId { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    public bool RequiresDeposit { get; init; } = true;

    public bool QueueEnabled { get; init; }

    public TimeOnly? QueueOpeningTime { get; init; }

    public string? Location { get; init; }
}

public sealed record UpdateRentableRequest
{
    public required string Name { get; init; }

    public required string Tag { get; init; }

    public required Guid UnitId { get; init; }

    public required Guid CategoryId { get; init; }

    public required Guid FamilyId { get; init; }

    public RentalAssetType RentalType { get; init; } = RentalAssetType.Location;

    public int TotalQuantity { get; init; } = 1;

    public bool RequiresDeposit { get; init; } = true;

    public bool QueueEnabled { get; init; }

    public TimeOnly? QueueOpeningTime { get; init; }

    public string? Location { get; init; }
}
