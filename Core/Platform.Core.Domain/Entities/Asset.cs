using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class Asset : Entity, ITenantScoped, IInventoryModuleEntity
{
    public required Guid TenantId { get; set; }

    public required Guid UnitId { get; set; }

    public required Guid CategoryId { get; set; }

    /// <summary>Platform asset family (spaces, electrical, goods, …).</summary>
    public required Guid FamilyId { get; set; }

    public required string Name { get; set; }

    public required string Tag { get; set; }

    public string? Location { get; set; }

    public string? SerialNumber { get; set; }

    public DateOnly? InstallationDate { get; set; }

    public required AssetStatus Status { get; set; }

    /// <summary>When true, this asset can be booked via the Rentals module.</summary>
    public bool IsRentable { get; set; }

    /// <summary>When true, this asset participates in PMOC / work-order maintenance flows.</summary>
    public bool RequiresMaintenance { get; set; }

    /// <summary>Family-specific attribute values validated against AssetFamily.FieldSchemaJson.</summary>
    public Dictionary<string, string?> Attributes { get; set; } = new(StringComparer.Ordinal);

    public DateTimeOffset? ScheduledDeletionAt { get; set; }

    public Unit Unit { get; set; } = null!;

    public AssetCategory Category { get; set; } = null!;

    public AssetFamily Family { get; set; } = null!;

    public RentalAsset? RentalConfiguration { get; set; }
}
