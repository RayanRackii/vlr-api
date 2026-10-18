using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Dashboard.Services;
using Platform.Api.Tests.Assets;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Dashboard;

public sealed class DashboardMetricsDefinitionTests
{
    [Fact]
    public async Task In_maintenance_counts_status_maintenance_only()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(
            harness.TenantProvider.TenantId!.Value,
            PlatformModules.Inventory,
            isActive: true));
        AddAsset(harness, "M1", AssetStatus.Maintenance, requiresMaintenance: false);
        AddAsset(harness, "M2", AssetStatus.Maintenance, requiresMaintenance: true);
        AddAsset(harness, "A1", AssetStatus.Active, requiresMaintenance: true);
        await harness.Db.SaveChangesAsync();

        var metrics = await CreateService(harness, new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);

        Assert.NotNull(metrics.Assets);
        Assert.Equal(2, metrics.Assets.InMaintenance);
        Assert.Equal(1, metrics.Assets.Active);
        Assert.Equal(3, metrics.Assets.Total);
    }

    [Fact]
    public async Task Work_orders_this_month_uses_brazil_civil_month_not_the_unfiltered_list()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = harness.TenantProvider.TenantId!.Value;
        harness.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.WorkOrders, isActive: true));
        var asset = AddAsset(harness, "Q1", AssetStatus.Active, requiresMaintenance: false);
        await harness.Db.SaveChangesAsync();

        for (var day = 1; day <= 6; day++)
        {
            AddWorkOrder(harness, asset.Id, new DateOnly(2026, 10, day), WorkOrderStatus.Pending);
        }

        AddWorkOrder(harness, asset.Id, new DateOnly(2026, 9, 30), WorkOrderStatus.Completed);
        AddWorkOrder(harness, asset.Id, new DateOnly(2026, 11, 1), WorkOrderStatus.Canceled);
        await harness.Db.SaveChangesAsync();

        var metrics = await CreateService(harness, new DateTimeOffset(2026, 10, 15, 15, 0, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);

        Assert.NotNull(metrics.WorkOrders);
        Assert.Equal(6, metrics.WorkOrders.TotalThisMonth);
        Assert.Equal(6, metrics.WorkOrders.Pending);
        Assert.Equal(8, await harness.Db.WorkOrders.CountAsync());
        Assert.Equal(
            metrics.WorkOrders.TotalThisMonth,
            metrics.WorkOrders.Pending
            + metrics.WorkOrders.InProgress
            + metrics.WorkOrders.Completed
            + metrics.WorkOrders.Canceled);
    }

    [Fact]
    public async Task Utc_october_first_at_02_is_still_september_in_brazil()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = harness.TenantProvider.TenantId!.Value;
        harness.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.WorkOrders, isActive: true));
        var asset = AddAsset(harness, "Q1", AssetStatus.Active, requiresMaintenance: false);
        await harness.Db.SaveChangesAsync();
        AddWorkOrder(harness, asset.Id, new DateOnly(2026, 9, 30), WorkOrderStatus.Pending);
        AddWorkOrder(harness, asset.Id, new DateOnly(2026, 10, 1), WorkOrderStatus.Pending);
        await harness.Db.SaveChangesAsync();

        var metrics = await CreateService(harness, new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);

        Assert.NotNull(metrics.WorkOrders);
        Assert.Equal(1, metrics.WorkOrders.TotalThisMonth);
        Assert.Equal(1, metrics.WorkOrders.Pending);
    }

    [Fact]
    public async Task Utc_october_first_at_02_is_still_september_in_brazil_for_rentals_revenue()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = harness.TenantProvider.TenantId!.Value;
        harness.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.Rentals, isActive: true));
        var customer = new Customer
        {
            TenantId = tenantId,
            Name = "Socio",
        };
        harness.Db.Customers.Add(customer);
        AddReservation(
            harness,
            customer.Id,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 9, 30), new TimeOnly(20, 0)),
            ReservationStatus.Confirmed,
            80m);
        AddReservation(
            harness,
            customer.Id,
            BrazilTimeZone.AtLocal(new DateOnly(2026, 10, 1), new TimeOnly(10, 0)),
            ReservationStatus.Confirmed,
            25m);
        await harness.Db.SaveChangesAsync();

        var metrics = await CreateService(harness, new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);

        Assert.NotNull(metrics.Rentals);
        Assert.Equal(80m, metrics.Rentals.ReservedRevenueThisMonth);
    }

    private static DashboardService CreateService(BulkCreateAssetsHarness harness, DateTimeOffset utcNow) =>
        new(harness.Db, harness.TenantProvider, new TestTimeProvider(utcNow));

    private static Asset AddAsset(
        BulkCreateAssetsHarness harness,
        string tag,
        AssetStatus status,
        bool requiresMaintenance)
    {
        var asset = new Asset
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            Name = tag,
            Tag = tag,
            Status = status,
            RequiresMaintenance = requiresMaintenance,
        };
        harness.Db.Assets.Add(asset);
        return asset;
    }

    private static void AddWorkOrder(
        BulkCreateAssetsHarness harness,
        Guid assetId,
        DateOnly scheduledDate,
        WorkOrderStatus status)
    {
        harness.Db.WorkOrders.Add(new WorkOrder
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            AssetId = assetId,
            Status = status,
            ScheduledDate = scheduledDate,
        });
    }

    private static void AddReservation(
        BulkCreateAssetsHarness harness,
        Guid customerId,
        DateTimeOffset start,
        ReservationStatus status,
        decimal totalAmount)
    {
        harness.Db.Reservations.Add(new Reservation
        {
            TenantId = harness.TenantProvider.TenantId!.Value,
            UnitId = harness.UnitId,
            CustomerId = customerId,
            CustomerName = "Socio",
            CustomerWhatsApp = "11999999999",
            StartDateTime = start,
            EndDateTime = start.AddHours(1),
            Status = status,
            TotalAmount = totalAmount,
            DepositPaid = 0m,
        });
    }
}
