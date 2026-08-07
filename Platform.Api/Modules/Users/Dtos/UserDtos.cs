namespace Platform.Api.Modules.Users.Dtos;

public static class ApplicationRoles
{
    public const string SuperAdmin = "SUPER_ADMIN";

    public const string Admin = "ADMIN";

    public const string Technician = "TECHNICIAN";

    public const string User = "USER";

    public const string Client = "CLIENT";
}

public sealed record CurrentUserResponse(
    Guid? Id,
    string FullName,
    string Email,
    string Role,
    Guid? TenantId,
    IReadOnlyList<string> ActiveModules,
    IReadOnlyList<string> ActiveAssetFamilies,
    bool IsTrial = false,
    DateTimeOffset? TrialEndsAt = null,
    DateTimeOffset? TrialPurgeAt = null,
    bool IsTrialReadOnly = false,
    bool NotificationsEmailOnly = false);

public sealed record TechnicianUserResponse(
    Guid Id,
    string FullName,
    string Email);
