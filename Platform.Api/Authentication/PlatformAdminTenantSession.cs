namespace Platform.Api.Authentication;

/// <summary>
/// Request-scoped effective tenant for a Platform Super-Admin.
/// Populated from the live Auth user and an active membership before authorization.
/// An unresolved session does not fall back to <c>app_metadata.tenant_id</c> on the access token.
/// </summary>
public sealed class PlatformAdminTenantSession
{
    public bool IsResolved { get; private set; }

    public Guid? TenantId { get; private set; }

    public void Resolve(Guid? tenantId)
    {
        TenantId = tenantId;
        IsResolved = true;
    }
}
