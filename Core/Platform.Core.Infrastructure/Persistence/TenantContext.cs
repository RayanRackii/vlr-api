namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Adapter over <see cref="ITenantProvider"/>: an active <see cref="PlatformScope"/> wins,
/// then the provider tenant, else <see cref="TenantContextMode.None"/>.
/// Read on every access (the provider can change during a request, e.g. ambient binders).
/// </summary>
public sealed class TenantContext(ITenantProvider tenantProvider) : ITenantContext
{
    public TenantContextMode Mode => PlatformScope.IsActive
        ? TenantContextMode.Platform
        : tenantProvider.TenantId is null ? TenantContextMode.None : TenantContextMode.Tenant;

    public Guid? TenantId => PlatformScope.IsActive ? null : tenantProvider.TenantId;
}
