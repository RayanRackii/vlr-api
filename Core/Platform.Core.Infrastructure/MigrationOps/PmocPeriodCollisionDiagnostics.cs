using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Core.Infrastructure.MigrationOps;

public sealed record PmocPeriodCollisionSample(
    Guid TenantId,
    Guid MaintenancePlanId,
    Guid AssetId,
    DateOnly ScheduledDate,
    IReadOnlyList<Guid> WorkOrderIds);

public sealed record PmocPeriodCollisionCounts(
    int CollisionGroups,
    IReadOnlyList<PmocPeriodCollisionSample> Samples);

public static class PmocPeriodCollisionDiagnostics
{
    public static async Task<PmocPeriodCollisionCounts> CollectAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.WorkOrders
            .IgnoreQueryFilters()
            .Where(workOrder =>
                workOrder.MaintenancePlanId != null
                && workOrder.Status != WorkOrderStatus.Canceled)
            .Select(workOrder => new PmocPeriodCollisionRow(
                workOrder.TenantId,
                workOrder.MaintenancePlanId!.Value,
                workOrder.AssetId,
                workOrder.ScheduledDate,
                workOrder.Id,
                workOrder.CreatedAt))
            .ToListAsync(cancellationToken);

        return Aggregate(rows);
    }

    public static PmocPeriodCollisionCounts Aggregate(
        IReadOnlyList<PmocPeriodCollisionRow> rows)
    {
        var groups = rows
            .GroupBy(row => (row.TenantId, row.MaintenancePlanId, row.AssetId, row.ScheduledDate))
            .Where(group => group.Count() > 1)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key.ScheduledDate)
            .ToList();

        var samples = groups
            .Take(10)
            .Select(group => new PmocPeriodCollisionSample(
                group.Key.TenantId,
                group.Key.MaintenancePlanId,
                group.Key.AssetId,
                group.Key.ScheduledDate,
                group.OrderBy(row => row.CreatedAt).Select(row => row.Id).ToList()))
            .ToList();

        return new PmocPeriodCollisionCounts(groups.Count, samples);
    }

    public sealed record PmocPeriodCollisionRow(
        Guid TenantId,
        Guid MaintenancePlanId,
        Guid AssetId,
        DateOnly ScheduledDate,
        Guid Id,
        DateTimeOffset CreatedAt);
}
