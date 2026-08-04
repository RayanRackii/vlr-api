using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Weekly pattern row that materializes into dated <see cref="Slot"/>s.
/// Schema: rentals.
/// </summary>
public class ScheduleTemplate : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid RentalAssetId { get; set; }

    public required DayOfWeek DayOfWeek { get; set; }

    public required TimeOnly StartTime { get; set; }

    public required TimeOnly EndTime { get; set; }

    public required Guid OccupancyKindId { get; set; }

    public string? Label { get; set; }

    public required bool IsActive { get; set; }

    public RentalAsset RentalAsset { get; set; } = null!;

    public OccupancyKind OccupancyKind { get; set; } = null!;
}
