namespace Platform.Api.Authorization;

public interface IPermissionResolver
{
    Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(
        Guid tenantId,
        Guid userId,
        string permissionKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<string>> GetEnabledCatalogKeysAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
