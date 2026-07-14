namespace Platform.Core.Infrastructure.Persistence;

/// <summary>
/// Bypasses tenant isolation for migrations, seeding, and super-admin operations.
/// </summary>
public sealed class NullTenantProvider : ITenantProvider
{
    public Guid? TenantId => null;
}
