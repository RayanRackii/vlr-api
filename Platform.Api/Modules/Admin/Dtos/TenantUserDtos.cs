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

    /// <summary>Defaults to Admin when <see cref="RoleIds"/> is omitted.</summary>
    public string? RoleName { get; init; }

    /// <summary>Optional additive role ids. When present and non-empty, these are assigned on accept.</summary>
    public IReadOnlyList<Guid>? RoleIds { get; init; }
}

public sealed record PromoteTenantUserRequestDto
{
    /// <summary>System role to grant (Admin, Technician, …).</summary>
    public required string RoleName { get; init; }
}

public sealed record InvitePreviewRequestDto
{
    public required string Token { get; init; }
}

public sealed record InvitePreviewResponseDto(
    string Email,
    string FullName,
    Guid TenantId,
    string TenantName,
    bool ExistingAccount,
    DateTimeOffset ExpiresAt);

public sealed record AcceptInviteRequestDto
{
    public required string Token { get; init; }

    /// <summary>
    /// Required only when the invited email has no staff account yet.
    /// Ignored when the email already has an account: that path never changes the password.
    /// </summary>
    public string? Password { get; init; }
}

public sealed record AcceptInviteResponseDto(
    Guid UserId,
    Guid TenantId,
    string Email,
    bool ExistingAccount = false);
