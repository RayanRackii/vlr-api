using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Immutable snapshot of a product's resource requirement, captured onto an order item when
/// the order is created. Later edits to <see cref="CatalogProductResourceRequirement"/> never
/// change already-placed orders.
/// </summary>
public class CatalogOrderItemResourceRequirement : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid OrderItemId { get; set; }

    /// <summary>Nullable: the source product requirement row at snapshot time, if any.</summary>
    public Guid? SourceRequirementId { get; set; }

    /// <summary>The planned asset (slice API-5). Backfilled from the rental asset on existing rows.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>Set when the asset is rental-backed; kept so rentals readers keep working.</summary>
    public Guid? RentalAssetId { get; set; }

    public Guid? AssetCategoryId { get; set; }

    public required int ResourceQuantity { get; set; }

    public required int DurationMinutesPerUnit { get; set; }

    public int PrepMinutes { get; set; }

    public int CleanupMinutes { get; set; }

    public CatalogOrderItem OrderItem { get; set; } = null!;

    public CatalogProductResourceRequirement? SourceRequirement { get; set; }
}
