using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Placement of one Rentable on a <see cref="RentalLayout"/> canvas (normalized %).
/// Schema: rentals.
/// </summary>
public class RentalLayoutItem : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid LayoutId { get; set; }

    public required Guid RentalAssetId { get; set; }

    /// <summary>Left edge as percent of canvas width (0–100).</summary>
    public required double XPercent { get; set; }

    /// <summary>Top edge as percent of canvas height (0–100).</summary>
    public required double YPercent { get; set; }

    public required double WidthPercent { get; set; }

    public required double HeightPercent { get; set; }

    public required int ZIndex { get; set; }

    public RentalLayout Layout { get; set; } = null!;

    public RentalAsset RentalAsset { get; set; } = null!;
}
