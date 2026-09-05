namespace Platform.Api.Authorization;

/// <summary>
/// Request-scoped commercial module entitlements for the current tenant.
/// Empty when <c>ITenantProvider.TenantId</c> is null. Not cached across requests.
/// </summary>
public interface ITenantModuleAccessor
{
    Task<IReadOnlySet<string>> GetActiveModuleKeysAsync(
        CancellationToken cancellationToken = default);
}
