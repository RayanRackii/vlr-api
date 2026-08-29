namespace Platform.Api.Modules.Users.Dtos;

public static class ApplicationRoles
{
    public const string SuperAdmin = "SUPER_ADMIN";

    public const string Admin = "ADMIN";

    public const string Technician = "TECHNICIAN";

    public const string User = "USER";

    public const string Client = "CLIENT";
}

public sealed record CurrentUserRoleDto(
    Guid Id,
    string Name,
    bool IsSystemRole);

public sealed record CurrentUserResponse(
    Guid? Id,
    string FullName,
    string Email,
    string Role,
    Guid? TenantId,
    IReadOnlyList<string> ActiveModules,
    IReadOnlyList<string> ActiveAssetFamilies,
    bool IsTrial,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? TrialPurgeAt,
    bool IsTrialReadOnly,
    bool NotificationsEmailOnly,
    IReadOnlyList<CurrentUserRoleDto> Roles,
    IReadOnlyList<string> Permissions);

public sealed record TechnicianUserResponse(
    Guid Id,
    string FullName,
    string Email);

public sealed record TenantMemberResponse(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    IReadOnlyList<CurrentUserRoleDto> Roles);

public sealed record InviteTenantMemberRequest(
    string FullName,
    string Email,
    IReadOnlyList<Guid> RoleIds);

public sealed record InviteTenantMemberResponse(
    Guid Id,
    string FullName,
    string Email,
    string RoleName,
    DateTimeOffset ExpiresAt);

public sealed record AssignUserRolesRequest(IReadOnlyList<Guid> RoleIds);
