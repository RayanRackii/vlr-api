namespace Platform.Core.Domain.Enums;

/// <summary>Where an <see cref="Entities.AssetAllocation"/> originated.</summary>
public enum AssetAllocationSource
{
    Manual = 0,
    CatalogOrder = 1,
    WorkOrder = 2,
}
