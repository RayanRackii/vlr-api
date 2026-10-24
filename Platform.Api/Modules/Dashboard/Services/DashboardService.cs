using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Dashboard.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Dashboard.Services;

public sealed class DashboardService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    TimeProvider timeProvider) : IDashboardService
{
    public async Task<DashboardMetricsDto> GetMetricsAsync(CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var utcNow = timeProvider.GetUtcNow();
        var utcTodayStart = new DateTimeOffset(utcNow.UtcDateTime.Date, TimeSpan.Zero);
        var last7Start = utcTodayStart.AddDays(-6);
        var last30Start = utcTodayStart.AddDays(-29);
        var zone = await TenantTimeZoneProvider.ResolveForTenantAsync(
            dbContext, tenantProvider.TenantId!.Value, cancellationToken);
        var civilToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime);
        var (todayStart, tomorrowStart) = TenantTimeZoneProvider.CivilDay(civilToday, zone);
        var monthStart = new DateOnly(civilToday.Year, civilToday.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);
        var confirmationWindowStart = utcTodayStart.AddDays(-6);

        var moduleNames = await dbContext.TenantModules
            .AsNoTracking()
            .Where(m => m.IsActive)
            .Select(m => m.ModuleName)
            .ToListAsync(cancellationToken);

        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in moduleNames)
        {
            if (PlatformModules.TryNormalize(name, out var canonical))
            {
                modules.Add(canonical);
            }
        }

        var customerActivity = await BuildCustomerActivityAsync(
            todayStart,
            last7Start,
            last30Start,
            cancellationToken);

        AssetMetricsDto? assets = null;
        if (modules.Contains(PlatformModules.Inventory))
        {
            assets = await BuildAssetMetricsAsync(cancellationToken);
        }

        WorkOrderMetricsDto? workOrders = null;
        if (modules.Contains(PlatformModules.WorkOrders))
        {
            workOrders = await BuildWorkOrderMetricsAsync(monthStart, nextMonthStart, cancellationToken);
        }

        PmocMetricsDto? pmoc = null;
        if (modules.Contains(PlatformModules.Pmoc))
        {
            pmoc = await BuildPmocMetricsAsync(monthStart, nextMonthStart, cancellationToken);
        }

        MaintenanceMetricsDto? maintenance = null;
        if (modules.Contains(PlatformModules.Maintenance))
        {
            maintenance = await BuildMaintenanceMetricsAsync(cancellationToken);
        }

        RentalsMetricsDto? rentals = null;
        if (modules.Contains(PlatformModules.Rentals))
        {
            rentals = await BuildRentalsMetricsAsync(
                civilToday,
                todayStart,
                tomorrowStart,
                confirmationWindowStart,
                monthStart,
                nextMonthStart,
                zone,
                cancellationToken);
        }

        return new DashboardMetricsDto(
            customerActivity,
            assets,
            workOrders,
            pmoc,
            maintenance,
            rentals);
    }

    private async Task<CustomerActivityMetricsDto> BuildCustomerActivityAsync(
        DateTimeOffset todayStart,
        DateTimeOffset last7Start,
        DateTimeOffset last30Start,
        CancellationToken cancellationToken)
    {
        var totalCustomers = await dbContext.Customers.CountAsync(cancellationToken);

        var loggedInToday = await dbContext.Customers
            .CountAsync(c => c.LastLoginAt >= todayStart, cancellationToken);

        var loggedInLast7Days = await dbContext.Customers
            .CountAsync(c => c.LastLoginAt >= last7Start, cancellationToken);

        var loggedInLast30Days = await dbContext.Customers
            .CountAsync(c => c.LastLoginAt >= last30Start, cancellationToken);

        return new CustomerActivityMetricsDto(
            loggedInToday,
            loggedInLast7Days,
            loggedInLast30Days,
            totalCustomers);
    }

    private async Task<AssetMetricsDto> BuildAssetMetricsAsync(CancellationToken cancellationToken)
    {
        var totalAssets = await dbContext.Assets.CountAsync(cancellationToken);
        var activeAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Active, cancellationToken);
        var maintenanceAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Maintenance, cancellationToken);
        var inactiveAssets = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Inactive, cancellationToken);

        var activeFamilyIds = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .Select(t => t.FamilyId)
            .ToListAsync(cancellationToken);

        var families = await dbContext.AssetFamilies
            .AsNoTracking()
            .Where(f => activeFamilyIds.Contains(f.Id) && f.IsActive)
            .OrderBy(f => f.SortOrder)
            .Select(f => new { f.Id, f.Key, f.Label })
            .ToListAsync(cancellationToken);

        var countsByFamily = await dbContext.Assets
            .AsNoTracking()
            .Where(a => activeFamilyIds.Contains(a.FamilyId))
            .GroupBy(a => a.FamilyId)
            .Select(g => new { FamilyId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var countMap = countsByFamily.ToDictionary(x => x.FamilyId, x => x.Count);

        var byFamily = families
            .Select(f => new AssetFamilyCountDto(
                f.Key,
                f.Label,
                countMap.GetValueOrDefault(f.Id)))
            .ToList();

        return new AssetMetricsDto(
            totalAssets,
            activeAssets,
            maintenanceAssets,
            inactiveAssets,
            byFamily);
    }

    private async Task<WorkOrderMetricsDto> BuildWorkOrderMetricsAsync(
        DateOnly monthStart,
        DateOnly nextMonthStart,
        CancellationToken cancellationToken)
    {
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

        return new WorkOrderMetricsDto(
            totalThisMonth,
            pending,
            inProgress,
            completed,
            canceled);
    }

    private async Task<PmocMetricsDto> BuildPmocMetricsAsync(
        DateOnly monthStart,
        DateOnly nextMonthStart,
        CancellationToken cancellationToken)
    {
        var activePlans = await dbContext.MaintenancePlans
            .CountAsync(p => p.IsActive, cancellationToken);

        var workOrdersFromPlans = await dbContext.WorkOrders
            .CountAsync(
                w => w.MaintenancePlanId != null
                     && w.ScheduledDate >= monthStart
                     && w.ScheduledDate < nextMonthStart,
                cancellationToken);

        var hasElectrical = await dbContext.TenantAssetFamilies
            .AsNoTracking()
            .AnyAsync(
                t => t.FamilyId == AssetFamilyKeys.Ids.Electrical,
                cancellationToken);

        int? electricalTotal = null;
        if (hasElectrical)
        {
            electricalTotal = await dbContext.Assets
                .CountAsync(a => a.FamilyId == AssetFamilyKeys.Ids.Electrical, cancellationToken);
        }

        return new PmocMetricsDto(activePlans, workOrdersFromPlans, electricalTotal);
    }

    private async Task<MaintenanceMetricsDto> BuildMaintenanceMetricsAsync(
        CancellationToken cancellationToken)
    {
        var assetsInMaintenance = await dbContext.Assets
            .CountAsync(a => a.Status == AssetStatus.Maintenance, cancellationToken);

        var openWorkOrders = await dbContext.WorkOrders
            .CountAsync(
                w => w.Status == WorkOrderStatus.Pending
                     || w.Status == WorkOrderStatus.InProgress,
                cancellationToken);

        return new MaintenanceMetricsDto(assetsInMaintenance, openWorkOrders);
    }

    private async Task<RentalsMetricsDto> BuildRentalsMetricsAsync(
        DateOnly today,
        DateTimeOffset todayStart,
        DateTimeOffset tomorrowStart,
        DateTimeOffset confirmationWindowStart,
        DateOnly civilMonthStart,
        DateOnly civilNextMonthStart,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        var monthStart = TenantTimeZoneProvider.CivilDay(civilMonthStart, zone).Start;
        var nextMonthStart = TenantTimeZoneProvider.CivilDay(civilNextMonthStart, zone).Start;
        var reservationsToday = dbContext.Reservations
            .Where(r => r.StartDateTime >= todayStart && r.StartDateTime < tomorrowStart);

        var pendingToday = await reservationsToday
            .CountAsync(r => r.Status == ReservationStatus.PendingDeposit, cancellationToken);
        var confirmedToday = await reservationsToday
            .CountAsync(r => r.Status == ReservationStatus.Confirmed, cancellationToken);
        var canceledToday = await reservationsToday
            .CountAsync(r => r.Status == ReservationStatus.Canceled, cancellationToken);
        var completedToday = await reservationsToday
            .CountAsync(r => r.Status == ReservationStatus.Completed, cancellationToken);

        var confirmedLast7 = await dbContext.Reservations
            .CountAsync(
                r => r.Status == ReservationStatus.Confirmed
                     && (r.UpdatedAt ?? r.CreatedAt) >= confirmationWindowStart,
                cancellationToken);

        var canceledLast7 = await dbContext.Reservations
            .CountAsync(
                r => r.Status == ReservationStatus.Canceled
                     && (r.UpdatedAt ?? r.CreatedAt) >= confirmationWindowStart,
                cancellationToken);

        var denom = confirmedLast7 + canceledLast7;
        var confirmationRate = denom == 0
            ? 0d
            : Math.Round(100d * confirmedLast7 / denom, 1);

        var slotsAvailableToday = await dbContext.Slots
            .CountAsync(
                s => s.Date == today && s.Status == SlotStatus.Available,
                cancellationToken);
        var slotsBookedToday = await dbContext.Slots
            .CountAsync(
                s => s.Date == today && s.Status == SlotStatus.Booked,
                cancellationToken);

        var reservedRevenue = await dbContext.Reservations
            .Where(r =>
                (r.Status == ReservationStatus.PendingDeposit
                 || r.Status == ReservationStatus.Confirmed)
                && r.StartDateTime >= monthStart
                && r.StartDateTime < nextMonthStart)
            .SumAsync(r => (decimal?)r.TotalAmount, cancellationToken) ?? 0m;

        var rentableSpaces = await dbContext.Assets
            .CountAsync(
                a => a.IsRentable && a.FamilyId == AssetFamilyKeys.Ids.Spaces,
                cancellationToken);
        var rentableGoods = await dbContext.Assets
            .CountAsync(
                a => a.IsRentable && a.FamilyId == AssetFamilyKeys.Ids.Goods,
                cancellationToken);

        return new RentalsMetricsDto(
            pendingToday,
            confirmedToday,
            canceledToday,
            completedToday,
            confirmationRate,
            slotsAvailableToday,
            slotsBookedToday,
            reservedRevenue,
            rentableSpaces,
            rentableGoods);
    }

    private void EnsureTenantContext()
    {
        _ = tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }
}
