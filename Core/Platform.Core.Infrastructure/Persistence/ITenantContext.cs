namespace Platform.Core.Infrastructure.Persistence;

public enum TenantContextMode
{
    /// <summary>No tenant and no explicit platform scope.</summary>
    None,
    Tenant,
    /// <summary>Inside an explicit <see cref="PlatformScope"/>.</summary>
    Platform,
}

/// <summary>
/// Tenant isolation mode for the current flow (passo 5).
/// Runs in parallel to <see cref="ITenantProvider"/>; the EF filter still reads the provider.
/// </summary>
public interface ITenantContext
{
    TenantContextMode Mode { get; }

    /// <summary>Set only in <see cref="TenantContextMode.Tenant"/>.</summary>
    Guid? TenantId { get; }
}
