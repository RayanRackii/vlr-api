namespace Platform.Api.Modules.Roles.Dtos;

public sealed record RoleResponse(
    Guid Id,
    string Name,
    string? Description,
    bool IsSystemRole,
    IReadOnlyList<string> PermissionKeys);

public sealed record PermissionCatalogItemResponse(
    string Key,
    string Name,
    string? Description,
    string? ModuleKey,
    string Resource);

public sealed record CreateRoleRequest(
    string Name,
    string? Description,
    IReadOnlyList<string> PermissionKeys);

public sealed record PatchRoleRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? PermissionKeys);

public sealed record ReplaceRolePermissionsRequest(IReadOnlyList<string> PermissionKeys);
