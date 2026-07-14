namespace Platform.Api.Features.CreateTenant;

public sealed record CreateTenantResponse(
    Guid TenantId,
    Guid HeadquartersUnitId,
    Guid AdminUserId,
    Guid SuperAdminRoleId,
    string SupabaseAuthId);
