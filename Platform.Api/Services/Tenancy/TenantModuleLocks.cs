using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// The locks that serialize everything that changes which modules a tenant has or removes module data:
/// the admin tenant update (module sync, re-enable), the module data purge schedule/cancel and the daily
/// purge job. Always take them in this order inside the transaction: the <c>tenant-modules:{id}</c>
/// advisory lock first (the lock every allocation writer already takes), then the tenant row.
/// No-ops outside PostgreSQL (in-memory test provider).
/// </summary>
internal static class TenantModuleLocks
{
    internal static bool IsPostgres(AppDbContext dbContext) =>
        dbContext.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

    /// <summary>Advisory lock shared with allocation admission (<c>tenant-modules:{tenantId}</c>).</summary>
    internal static async Task LockModulesAsync(AppDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        if (!IsPostgres(dbContext))
            return;

        var key = $"tenant-modules:{tenantId}";
        const long seed = 0;
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, {seed}))",
            cancellationToken);
    }

    /// <summary>
    /// Locks the tenant row. <c>FOR NO KEY UPDATE</c> excludes the other lockers of this row
    /// (<c>TenantPurger</c> takes <c>FOR UPDATE</c>, which conflicts with it) without blocking the
    /// foreign-key inserts of unrelated tenant writers for the length of a large purge.
    /// </summary>
    internal static async Task LockTenantRowAsync(AppDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        if (!IsPostgres(dbContext))
            return;

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT id FROM core.tenants WHERE id = {tenantId} FOR NO KEY UPDATE",
            cancellationToken);
    }

    internal static async Task LockAsync(AppDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        await LockModulesAsync(dbContext, tenantId, cancellationToken);
        await LockTenantRowAsync(dbContext, tenantId, cancellationToken);
    }
}
