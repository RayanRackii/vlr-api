namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Request-scoped tenant override.
/// Precedence: ambient (B2C subdomain binder, Super-Admin support header) over JWT,
/// except a Customer JWT, whose tenant_id must match the ambient tenant.
/// </summary>
public sealed class AmbientTenantContext
{
    public Guid? TenantId { get; set; }
}
