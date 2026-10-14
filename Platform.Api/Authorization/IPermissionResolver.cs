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

    /// <summary>
    /// Module-enabled catalog keys conferred by the system Admin wildcard.
    /// <see cref="Platform.Core.Domain.Constants.PermissionCatalog.AdminWildcardExcludedKeys"/>
    /// stay active for an explicit grant and are absent from this set.
    /// This is not the full set of live permission keys.
    /// </summary>
    Task<IReadOnlySet<string>> GetAdminWildcardKeysAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}
