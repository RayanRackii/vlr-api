namespace Platform.Api.Authorization;

public sealed record RbacActor(
    Guid TenantId,
    Guid? UserId,
    bool IsPlatformAdminInTenant);
