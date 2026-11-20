using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Declares the resource(s) a catalog product's unit of sale consumes during production.
/// The target is either an asset (<see cref="AssetId"/>, plus <see cref="RentalAssetId"/> when that
/// asset is rental-backed, kept for old readers) or a category (<see cref="AssetCategoryId"/>). Only
/// asset targets are supported; category selection is accepted in schema but rejected by endpoints
/// with 400 "not supported yet". No rows for a product means ordinary (non-scheduled) product
/// behavior.
/// </summary>
public class CatalogProductResourceRequirement : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ProductId { get; set; }

    /// <summary>The planned asset (slice API-5). Backfilled from the rental asset on existing rows.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>Set when the asset is rental-backed; kept so rentals readers keep working.</summary>
    public Guid? RentalAssetId { get; set; }

    public Guid? AssetCategoryId { get; set; }

    public required int ResourceQuantity { get; set; }

    public required int DurationMinutesPerUnit { get; set; }

    public int PrepMinutes { get; set; }

    public int CleanupMinutes { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The <see cref="CatalogProduct.ResourceRequirementsVersion"/> this row belongs to.</summary>
    public required int Version { get; set; }

    public CatalogProduct Product { get; set; } = null!;
}
