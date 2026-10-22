namespace Platform.Api.Modules.Rentals;

/// <summary>Explicit operational scope for the Development-only demo seed; absent means denied.</summary>
public sealed class BookingMapDemoSeedOptions
{
    public const string SectionName = "BookingMaps:DemoSeed";
    public Guid[] AllowedTenantIds { get; set; } = [];

    public bool IsAllowed(Guid tenantId) => tenantId != Guid.Empty && AllowedTenantIds.Contains(tenantId);
}
