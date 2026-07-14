using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Dashboard.Dtos;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Dashboard.Services;

public sealed class DashboardService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IDashboardService
{
    public async Task<DashboardMetricsDto> GetMetricsAsync(CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var utcNow = DateTimeOffset.UtcNow;
        var monthStart = new DateOnly(utcNow.Year, utcNow.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        var totalAssets = await dbContext.Assets.CountAsync(cancellationToken);
        var activeAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Active, cancellationToken);
        var maintenanceAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Maintenance, cancellationToken);
        var inactiveAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Inactive, cancellationToken);

        var workOrdersThisMonth = dbContext.WorkOrders
            .Where(w =>
                w.ScheduledDate >= monthStart
                && w.ScheduledDate < nextMonthStart);

        var totalThisMonth = await workOrdersThisMonth.CountAsync(cancellationToken);
        var pending = await workOrdersThisMonth
            .CountAsync(w => w.Status == WorkOrderStatus.Pending, cancellationToken);
        var inProgress = await workOrdersThisMonth
            .CountAsync(w => w.Status == WorkOrderStatus.InProgress, cancellationToken);
        var completed = await workOrdersThisMonth
            .CountAsync(w => w.Status == WorkOrderStatus.Completed, cancellationToken);
        var canceled = await workOrdersThisMonth
            .CountAsync(w => w.Status == WorkOrderStatus.Canceled, cancellationToken);

        return new DashboardMetricsDto(
            new AssetMetricsDto(
                totalAssets,
                activeAssets,
                maintenanceAssets,
                inactiveAssets),
            new WorkOrderMetricsDto(
                totalThisMonth,
                pending,
                inProgress,
                completed,
                canceled));
    }

    private void EnsureTenantContext()
    {
        _ = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }
}
