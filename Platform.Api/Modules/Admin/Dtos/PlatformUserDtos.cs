namespace Platform.Api.Modules.Admin.Dtos;

public sealed record PlatformUserResponseDto(
    Guid Id,
    string FullName,
    string Email,
    bool IsActive,
    Guid TenantId,
    string TenantLegalName,
    string? TenantSubdomain,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);
