using Platform.Api.Authorization;
using Platform.Api.Modules.Roles.Dtos;

namespace Platform.Api.Modules.Roles.Services;

public interface IRoleService
{
    Task<IReadOnlyList<RoleResponse>> ListAsync(
        Guid tenantId,
        CancellationToken cancellationToken);

    Task<RoleResponse?> GetByIdAsync(
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken);

    Task<RoleResponse> CreateAsync(
        Guid tenantId,
        RbacActor actor,
        CreateRoleRequest request,
        CancellationToken cancellationToken);

    Task<RoleResponse> PatchAsync(
        Guid tenantId,
        RbacActor actor,
        Guid roleId,
        PatchRoleRequest request,
        CancellationToken cancellationToken);

    Task<RoleResponse> ReplacePermissionsAsync(
        Guid tenantId,
        RbacActor actor,
        Guid roleId,
        IReadOnlyList<string> permissionKeys,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        Guid tenantId,
        Guid roleId,
        CancellationToken cancellationToken);
}
