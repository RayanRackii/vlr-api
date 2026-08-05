namespace Platform.Api.Authentication;

public static class TenantHeaders
{
    public const string Subdomain = "X-Tenant-Subdomain";

    /// <summary>
    /// Platform Super-Admin support mode: scopes the request to this tenant
    /// (sets AmbientTenantContext).
    /// </summary>
    public const string SupportTenantId = "X-Support-Tenant-Id";
}
