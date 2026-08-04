namespace Platform.Api.Modules.Admin.Dtos;

public sealed record TenantUserResponseDto(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);

public sealed record TenantInviteResponseDto(
    Guid Id,
    string FullName,
    string Email,
    string RoleName,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    bool IsPending,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt);

public sealed record TenantUsersBundleDto(
    IReadOnlyList<TenantUserResponseDto> Users,
    IReadOnlyList<TenantInviteResponseDto> Invites);

public sealed record InviteTenantUserRequestDto
{
    public required string FullName { get; init; }

    public required string Email { get; init; }

    /// <summary>Defaults to Admin.</summary>
    public string? RoleName { get; init; }
}

public sealed record PromoteTenantUserRequestDto
{
    /// <summary>System role to grant (Admin, Technician, …).</summary>
    public required string RoleName { get; init; }
}

public sealed record AcceptInviteRequestDto
{
    public required string Token { get; init; }

    public required string Password { get; init; }
}

public sealed record AcceptInviteResponseDto(
    Guid UserId,
    Guid TenantId,
    string Email);
