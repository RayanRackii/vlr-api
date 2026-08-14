using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Rentals.Dtos;

// --- Occupancy kinds ---

public sealed record OccupancyKindResponseDto(
    Guid Id,
    string Key,
    string Label,
    string? ColorHex,
    bool IsBookableByCustomer,
    bool BlocksCapacity,
    int SortOrder,
    bool IsActive);

public sealed record UpsertOccupancyKindRequestDto
{
    public required string Key { get; init; }

    public required string Label { get; init; }

    public string? ColorHex { get; init; }

    public bool IsBookableByCustomer { get; init; }

    public bool BlocksCapacity { get; init; } = true;

    public int SortOrder { get; init; }

    public bool IsActive { get; init; } = true;
}

// --- Schedule templates ---

public sealed record ScheduleTemplateResponseDto(
    Guid Id,
    Guid RentalAssetId,
    string AssetName,
    DayOfWeek DayOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    Guid OccupancyKindId,
    string OccupancyKindLabel,
    string? Label,
    bool IsActive);

public sealed record UpsertScheduleTemplateRequestDto
{
    public required Guid RentalAssetId { get; init; }

    public required DayOfWeek DayOfWeek { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required Guid OccupancyKindId { get; init; }

    public string? Label { get; init; }

    public bool IsActive { get; init; } = true;
}

// --- Slots / day schedule ---

public enum SlotOccurrenceSource
{
    WeeklyDefault,
    DailyOverride,
}

public enum DailyOccurrenceAction
{
    Update,
    MakeUnavailable,
    RestoreWeeklyDefault,
}

public sealed record SlotResponseDto(
    Guid Id,
    Guid RentalAssetId,
    string AssetName,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly EndTime,
    Guid OccupancyKindId,
    string OccupancyKindKey,
    string OccupancyKindLabel,
    string? OccupancyKindColorHex,
    bool IsBookableByCustomer,
    string? Label,
    SlotStatus Status,
    Guid? ReservationId,
    bool IsDerived,
    SlotOccurrenceSource Source);

public sealed record DayScheduleResponseDto(
    DateOnly Date,
    IReadOnlyList<SlotResponseDto> Slots);

public sealed record ApplyDailyOccurrenceRequestDto
{
    /// <summary>Persisted slot id when known. Null for OpenHours-derived windows.</summary>
    public Guid? SlotId { get; init; }

    public required Guid RentalAssetId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required DailyOccurrenceAction Action { get; init; }

    /// <summary>Required for <see cref="DailyOccurrenceAction.Update"/>.</summary>
    public Guid? OccupancyKindId { get; init; }

    public string? Label { get; init; }
}

public sealed record PublishDayRequestDto
{
    public required DateOnly Date { get; init; }

    /// <summary>Optional: publish only this rentable; otherwise all SlotGrid rentables.</summary>
    public Guid? RentalAssetId { get; init; }

    /// <summary>Optional: publish these rentables (takes precedence over <see cref="RentalAssetId"/> when set).</summary>
    public IReadOnlyList<Guid>? RentalAssetIds { get; init; }
}

public sealed record SeedDefaultTemplatesRequestDto
{
    public Guid? RentalAssetId { get; init; }

    public IReadOnlyList<Guid>? RentalAssetIds { get; init; }

    /// <summary>Inclusive start of the hourly grid. Default 08:00.</summary>
    public TimeOnly? OpenTime { get; init; }

    /// <summary>Exclusive end of the hourly grid. Default 22:00.</summary>
    public TimeOnly? CloseTime { get; init; }

    /// <summary>Slot length in minutes. Default 60.</summary>
    public int SlotMinutes { get; init; } = 60;

    /// <summary>Defaults to the tenant's active bookable "open" kind.</summary>
    public Guid? OccupancyKindId { get; init; }
}

public sealed record SeedDefaultTemplatesResponseDto(int Created, int Skipped);

public sealed record UpsertSlotRequestDto
{
    public required Guid RentalAssetId { get; init; }

    public required DateOnly Date { get; init; }

    public required TimeOnly StartTime { get; init; }

    public required TimeOnly EndTime { get; init; }

    public required Guid OccupancyKindId { get; init; }

    public string? Label { get; init; }
}

public sealed record BookSlotRequestDto
{
    public required Guid SlotId { get; init; }

    public required Guid UnitId { get; init; }

    public int Quantity { get; init; } = 1;
}

// --- Layouts ---

public sealed record RentalLayoutItemResponseDto(
    Guid Id,
    Guid RentalAssetId,
    string AssetName,
    double XPercent,
    double YPercent,
    double WidthPercent,
    double HeightPercent,
    int ZIndex);

public sealed record RentalLayoutResponseDto(
    Guid Id,
    Guid? UnitId,
    string Name,
    bool IsActive,
    IReadOnlyList<RentalLayoutItemResponseDto> Items);

public sealed record UpsertRentalLayoutItemRequestDto
{
    public required Guid RentalAssetId { get; init; }

    public required double XPercent { get; init; }

    public required double YPercent { get; init; }

    public required double WidthPercent { get; init; }

    public required double HeightPercent { get; init; }

    public int ZIndex { get; init; }
}

public sealed record UpsertRentalLayoutRequestDto
{
    public Guid? UnitId { get; init; }

    public required string Name { get; init; }

    public bool IsActive { get; init; } = true;

    public IReadOnlyList<UpsertRentalLayoutItemRequestDto> Items { get; init; } = [];
}
