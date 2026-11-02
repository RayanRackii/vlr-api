using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Declares the resource(s) a catalog product's unit of sale consumes during production.
/// Exactly one of <see cref="RentalAssetId"/> or <see cref="AssetCategoryId"/> is set.
/// Phase 0 only supports <see cref="RentalAssetId"/>; category selection is accepted in
/// schema but rejected by endpoints with 400 "not supported yet". No rows for a product
/// means ordinary (non-scheduled) product behavior.
/// </summary>
public class CatalogProductResourceRequirement : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid ProductId { get; set; }

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
