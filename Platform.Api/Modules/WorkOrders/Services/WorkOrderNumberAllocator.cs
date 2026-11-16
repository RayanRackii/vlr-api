using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

/// <summary>
/// Allocates the next per-tenant work order number. Must run inside the transaction that
/// inserts the work order: the counter row stays locked until commit, so concurrent creates
/// for the same tenant serialize and never receive the same number (and a rollback gives the
/// number back).
/// </summary>
internal static class WorkOrderNumberAllocator
{
    public static async Task<int> AllocateAsync(
        AppDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsRelational())
        {
            // Single atomic upsert: creates the counter at 1 or increments it, taking the row
            // lock until the surrounding transaction ends. No read-then-write race.
            return await dbContext.Database
                .SqlQuery<int>(
                    $"""
                    INSERT INTO os.work_order_number_sequences (tenant_id, last_number)
                    VALUES ({tenantId}, 1)
                    ON CONFLICT (tenant_id)
                    DO UPDATE SET last_number = work_order_number_sequences.last_number + 1
                    RETURNING last_number AS "Value"
                    """)
                .SingleAsync(cancellationToken);
        }

        var sequence = await dbContext.WorkOrderNumberSequences
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (sequence is null)
        {
            sequence = new WorkOrderNumberSequence { TenantId = tenantId, LastNumber = 0 };
            dbContext.WorkOrderNumberSequences.Add(sequence);
        }

        sequence.LastNumber++;
        return sequence.LastNumber;
    }
}
