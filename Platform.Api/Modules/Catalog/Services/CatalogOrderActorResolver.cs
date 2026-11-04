using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Resolves the actor behind a catalog order transition the same way for every service that
/// records order history: an authenticated B2B user (matched by the Supabase auth subject) carries
/// their user id, and everything else (background jobs, missing context) is recorded as System.
/// </summary>
internal static class CatalogOrderActorResolver
{
    public static async Task<(CatalogActorType ActorType, Guid? ActorId)> ResolveAsync(
        IHttpContextAccessor? httpContextAccessor,
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        var sub = principal?.FindFirst("sub")?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        Guid? actorId = null;
        if (!string.IsNullOrWhiteSpace(sub))
        {
            actorId = await dbContext.Users
                .AsNoTracking()
                .Where(u => u.SupabaseAuthId == sub)
                .Select(u => (Guid?)u.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return actorId is null
            ? (CatalogActorType.System, null)
            : (CatalogActorType.B2BUser, actorId);
    }
}
