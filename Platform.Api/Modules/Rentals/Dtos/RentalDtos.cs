using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Platform.Api.Serialization;
using Platform.Core.Domain.Contracts.ResourceVisuals;
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
    bool RequiresDeposit,
    SchedulePolicy SchedulePolicy,
    TimeOnly? OpenTime,
    TimeOnly? CloseTime,
    string? AllowedDurationMinutes,
    bool QueueEnabled,
    TimeOnly? QueueOpeningTime,
    Guid? CategoryId,
    string? CategoryName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    int? MaxAdvanceBookingDays,
    int? EffectiveMaxAdvanceBookingDays,
    Guid? VisualTemplateRevisionId = null,
    int? CapacityPeople = null,
    int? CancelCutoffHours = null,
    Guid? FamilyId = null,
    int? EffectiveCancelCutoffHours = null,
    string? Location = null,
    DateTimeOffset? ScheduledDeletionAt = null);

public sealed record UpdateRentalSchedulePolicyRequestDto
{
    public required SchedulePolicy SchedulePolicy { get; init; }

    public TimeOnly? OpenTime { get; init; }

    public TimeOnly? CloseTime { get; init; }

    /// <summary>Comma-separated minutes for OpenHours (e.g. "60" or "60,120").</summary>
    public string? AllowedDurationMinutes { get; init; }

    /// <summary>Omitted keeps the override; explicit null restores tenant inheritance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CancelCutoffHours { get; init; }
}

public sealed record BulkUpdateRentalSchedulePolicyRequestDto
{
    public required IReadOnlyList<Guid> RentalAssetIds { get; init; }

    public required SchedulePolicy SchedulePolicy { get; init; }

    public TimeOnly? OpenTime { get; init; }

    public TimeOnly? CloseTime { get; init; }

    /// <summary>Comma-separated minutes for OpenHours (e.g. "60" or "60,120").</summary>
    public string? AllowedDurationMinutes { get; init; }

    /// <summary>Omitted keeps the override; explicit null restores tenant inheritance.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonOptionalInt32Converter))]
    public JsonOptional<int> CancelCutoffHours { get; init; }
}

public sealed record BulkUpdateRentalSchedulePolicyResponseDto(
    int Updated,
    IReadOnlyList<RentalAssetResponse> Items);

public sealed record CheckAvailabilityRequestDto
{
    public required Guid AssetId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>Inclusive last civil day; omitted/null means Date.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? EndDate { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public int Quantity { get; init; } = 1;
}

public sealed record CheckAvailabilityResponseDto(
    bool IsAvailable,
    int RequestedQuantity,
    int AvailableQuantity,
    decimal? EstimatedTotalAmount,
    string? Reason,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
    PricingUnit? PricingUnit = null);

public sealed record CreateReservationItemRequestDto
{
    public required Guid AssetId { get; init; }

    public int Quantity { get; init; } = 1;
}

public sealed record CreateReservationRequestDto
{
    public required Guid UnitId { get; init; }

    public required DateOnly Date { get; init; }

    /// <summary>Inclusive last civil day; omitted/null means Date. Daily limits count the start date only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateOnly? EndDate { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required IReadOnlyList<CreateReservationItemRequestDto> Items { get; init; }

    /// <summary>Optional quoted total in BRL; omitted/null keeps legacy booking behavior.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? ExpectedTotalAmount { get; init; }
}

public sealed record ReservationItemResponseDto(
    Guid Id,
    Guid AssetId,
    Guid RentalAssetId,
    string AssetName,
    int Quantity,
    decimal UnitPrice,
    decimal SubTotal,
    PricingUnit? PricingUnit = null,
    int? Days = null);

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
    IReadOnlyList<ReservationItemResponseDto> Items,
    DateTimeOffset? CancelDeadlineUtc = null);

public sealed record UpdateRentalPricingDto
{
    public required DayOfWeek DayOfWeek { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required decimal PricePerHour { get; init; }

    /// <summary>Omitted or null preserves the current pricing unit.</summary>
    [EnumDataType(typeof(PricingUnit))]
    [JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
    public PricingUnit? PricingUnit { get; init; }

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
    DateTimeOffset? UpdatedAt,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
    PricingUnit PricingUnit = PricingUnit.Hour);

public sealed record BulkApplyPricingsRequest
{
    public required IReadOnlyList<Guid> AssetIds { get; init; }

    public required IReadOnlyList<BulkPricingRowDto> Pricings { get; init; }

    public bool Replace { get; init; }
}

public sealed record BulkPricingRowDto
{
    public required DayOfWeek DayOfWeek { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required decimal PricePerHour { get; init; }

    [EnumDataType(typeof(PricingUnit))]
    [JsonConverter(typeof(JsonStringEnumConverter<PricingUnit>))]
    public PricingUnit PricingUnit { get; init; } = PricingUnit.Hour;

    public required bool RequiresDeposit { get; init; }

    public required decimal DepositPercentage { get; init; }
}

public sealed record BulkApplyPricingsResponse(int AppliedAssetCount, int PricingsCreated);

public sealed record ReservationQueueTicketDto(
    Guid Id,
    QueueTicketStatus Status,
    long Sequence,
    int Position,
    DateTimeOffset JoinedAt,
    DateTimeOffset? TurnStartedAt,
    DateTimeOffset? TurnExpiresAt,
    Guid? CompletedReservationId);

public sealed record ReservationQueueStatusDto(
    Guid RentalAssetId,
    bool QueueEnabled,
    DateOnly OpeningDate,
    DateTimeOffset OpensAt,
    DateTimeOffset WaitingRoomOpensAt,
    DateTimeOffset ServerNow,
    QueuePhase Phase,
    int WaitingCount,
    int AheadCount,
    ReservationQueueTicketDto? MyTicket);
