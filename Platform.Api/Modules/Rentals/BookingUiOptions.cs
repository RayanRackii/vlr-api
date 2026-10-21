namespace Platform.Api.Modules.Rentals;

/// <summary>Deployment configuration, scoped by immutable tenant UUID; absent means off.</summary>
public sealed class BookingUiOptions
{
    public const string SectionName = "Rentals:BookingUi";
    public Guid[] EnabledTenantIds { get; set; } = [];

    public bool IsEnabled(Guid tenantId) => tenantId != Guid.Empty && EnabledTenantIds.Contains(tenantId);
}
