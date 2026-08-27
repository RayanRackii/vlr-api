using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Authorization;

public interface IRbacActorAccessor
{
    Task<RbacActor> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
}

public sealed class RbacActorAccessor(
    ITenantProvider tenantProvider,
    IPlatformAdminChecker platformAdminChecker,
    AppDbContext dbContext) : IRbacActorAccessor
{
    public async Task<RbacActor> GetAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");

        var isPlatformAdmin = platformAdminChecker.IsPlatformAdmin(principal);
        var supabaseAuthId = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        Guid? userId = null;
        if (!string.IsNullOrWhiteSpace(supabaseAuthId))
        {
            userId = await dbContext.Users
                .AsNoTracking()
                .Where(user =>
                    user.TenantId == tenantId
                    && user.SupabaseAuthId == supabaseAuthId
                    && user.IsActive)
                .Select(user => (Guid?)user.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new RbacActor(tenantId, userId, isPlatformAdmin);
    }
}
