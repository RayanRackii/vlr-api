using System.Text.Json;
using System.Text.Json.Serialization;

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

/// <summary>
/// B2B session profile from <c>GET /api/users/me</c>.
/// TenantTradeName and TenantLegalName are the caller's own tenant, or null when
/// that caller has no tenant. Blank stored names are returned as null.
/// Phone is the caller's own staff contact number in E.164, or null.
/// </summary>
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
    IReadOnlyList<string> Permissions,
    string? TenantTradeName = null,
    string? TenantLegalName = null,
    string? Phone = null,
    string TimeZoneId = "America/Sao_Paulo");

/// <summary>
/// Body of <c>PUT /api/users/me</c>. <c>phone</c> must be present.
/// Null clears the staff contact number. A string is validated as E.164.
/// Any other property, including a user id, is ignored.
/// </summary>
[JsonConverter(typeof(UpdateCurrentUserPhoneRequestConverter))]
public sealed class UpdateCurrentUserPhoneRequest
{
    [JsonRequired]
    public string? Phone { get; init; }

    [JsonIgnore]
    public bool IsSpecified { get; init; }

    [JsonIgnore]
    public bool IsInvalid { get; init; }
}

public sealed class UpdateCurrentUserPhoneRequestConverter : JsonConverter<UpdateCurrentUserPhoneRequest>
{
    public override UpdateCurrentUserPhoneRequest Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("phone", out var phone))
        {
            return new UpdateCurrentUserPhoneRequest();
        }

        if (phone.ValueKind == JsonValueKind.Null)
        {
            return new UpdateCurrentUserPhoneRequest { IsSpecified = true };
        }

        if (phone.ValueKind != JsonValueKind.String)
        {
            return new UpdateCurrentUserPhoneRequest { IsSpecified = true, IsInvalid = true };
        }

        return new UpdateCurrentUserPhoneRequest
        {
            Phone = phone.GetString(),
            IsSpecified = true,
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        UpdateCurrentUserPhoneRequest value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        if (value.Phone is null)
        {
            writer.WriteNull("phone");
        }
        else
        {
            writer.WriteString("phone", value.Phone);
        }

        writer.WriteEndObject();
    }
}

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

public sealed record StaffTenantMembershipDto(
    Guid TenantId,
    string LegalName,
    string? TradeName,
    string? Subdomain);

public sealed record StaffTenantMembershipListResponse(
    Guid? AccessTokenTenantId,
    Guid? AuthMetadataTenantId,
    bool SessionRefreshRequired,
    IReadOnlyList<StaffTenantMembershipDto> Memberships);

public sealed record SelectStaffTenantRequest(Guid TenantId);

public sealed record SelectStaffTenantResponse(
    Guid TenantId,
    string LegalName,
    string? TradeName,
    string? Subdomain,
    bool SessionRefreshRequired);
