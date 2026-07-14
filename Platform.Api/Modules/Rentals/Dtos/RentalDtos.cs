using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Dtos;

public sealed record RentalAssetResponse(
    Guid Id,
    Guid AssetId,
    Guid TenantId,
    Guid UnitId,
    string Name,
    RentalAssetType Type,
    int TotalQuantity,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public sealed record CheckAvailabilityRequestDto
{
    public required Guid AssetId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public int Quantity { get; init; } = 1;
}

public sealed record CheckAvailabilityResponseDto(
    bool IsAvailable,
    int RequestedQuantity,
    int AvailableQuantity,
    decimal? EstimatedTotalAmount,
    string? Reason);

public sealed record CreateReservationItemRequestDto
{
    public required Guid AssetId { get; init; }

    public int Quantity { get; init; } = 1;
}

public sealed record CreateReservationRequestDto
{
    public required Guid UnitId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required IReadOnlyList<CreateReservationItemRequestDto> Items { get; init; }
}

public sealed record ReservationItemResponseDto(
    Guid Id,
    Guid AssetId,
    Guid RentalAssetId,
    string AssetName,
    int Quantity,
    decimal UnitPrice,
    decimal SubTotal);

public sealed record ReservationResponseDto(
    Guid Id,
    Guid TenantId,
    Guid UnitId,
    Guid CustomerId,
    string CustomerName,
    string CustomerWhatsApp,
    DateTimeOffset StartDateTime,
    DateTimeOffset EndDateTime,
    ReservationStatus Status,
    decimal TotalAmount,
    decimal DepositPaid,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ReservationItemResponseDto> Items);

public sealed record CreateRentalPricingDto
{
    public required DayOfWeek DayOfWeek { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required decimal PricePerHour { get; init; }

    public bool RequiresDeposit { get; init; }

    public decimal DepositPercentage { get; init; }
}

public sealed record UpdateRentalPricingDto
{
    public required DayOfWeek DayOfWeek { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required decimal PricePerHour { get; init; }

    public required bool RequiresDeposit { get; init; }

    public required decimal DepositPercentage { get; init; }
}

public sealed record RentalPricingResponseDto(
    Guid Id,
    Guid TenantId,
    Guid AssetId,
    Guid RentalAssetId,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    decimal PricePerHour,
    bool RequiresDeposit,
    decimal DepositPercentage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
