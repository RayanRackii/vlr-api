using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

internal static class TestPermissionResolvers
{
    public static PermissionResolver Create(AppDbContext db, ITenantProvider tenantProvider) =>
        new(db, new TenantModuleAccessor(db, tenantProvider), NullLogger<PermissionResolver>.Instance);
}
