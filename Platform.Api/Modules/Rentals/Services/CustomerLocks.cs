using Microsoft.EntityFrameworkCore;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Rentals.Services;

internal static class CustomerLocks
{
    public static Task LockAsync(
        AppDbContext db,
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id is required to lock a customer.");
        }

        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id is required to lock a customer.");
        }

        if (!db.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        return db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            SELECT 1
            FROM core.customers
            WHERE id = {customerId}
              AND tenant_id = {tenantId}
            FOR UPDATE
            """,
            cancellationToken);
    }
}
