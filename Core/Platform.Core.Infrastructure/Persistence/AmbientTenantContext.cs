namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Request-scoped tenant override.
/// Precedence: ambient (B2C subdomain binder, Super-Admin support header) over JWT.
/// </summary>
public sealed class AmbientTenantContext
{
    public Guid? TenantId { get; set; }
}
