using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Core.Domain.Constants;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// Serializes planning writes against disabling the module. Takes the
/// <c>tenant-modules:{tenantId}</c> advisory transaction lock that
/// <c>AdminTenantService.LockTenantModulesAsync</c> and the catalog allocation flow take, then
/// re-reads, inside the transaction, that <c>resource-planning</c> is still active. Without it a
/// write that passed the module filter could commit after a disable whose guard saw no allocation.
/// Lock order (spec 4.3): this lock first, then the asset rows.
/// </summary>
internal static class PlanningModuleGuard
{
    public static async Task LockAndRequireActiveAsync(
        AppDbContext db,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock tenant modules.");
        }

        if (db.Database.IsRelational()
            && db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            var key = $"tenant-modules:{tenantId}";
            const long seed = 0;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, {seed}))",
                cancellationToken);
        }

        var active = await db.TenantModules
            .AsNoTracking()
            .AnyAsync(
                m => m.TenantId == tenantId && m.IsActive && m.ModuleName == PlatformModules.ResourcePlanning,
                cancellationToken);
        if (!active)
        {
            throw new PlanningException(
                StatusCodes.Status403Forbidden,
                RequireActiveModuleAttribute.InactiveModuleCode,
                RequireActiveModuleAttribute.InactiveModuleError);
        }
    }
}
