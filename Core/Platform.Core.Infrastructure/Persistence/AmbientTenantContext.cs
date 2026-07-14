namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Request-scoped tenant override (e.g. B2C public routes resolved by subdomain).
/// Takes precedence over JWT when set.
/// </summary>
public sealed class AmbientTenantContext
{
    public Guid? TenantId { get; set; }
}
