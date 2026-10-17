using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

internal static class WorkOrderRowLock
{
    public static Task AcquireAsync(
        AppDbContext db,
        Guid tenantId,
        Guid workOrderId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock work orders.");
        }

        if (!IsPostgres(db))
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT id
            FROM os.work_orders
            WHERE id = {workOrderId}
              AND tenant_id = {tenantId}
            FOR UPDATE
            """,
            cancellationToken);
    }

    private static bool IsPostgres(AppDbContext db) =>
        db.Database.IsRelational()
        && db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
}
