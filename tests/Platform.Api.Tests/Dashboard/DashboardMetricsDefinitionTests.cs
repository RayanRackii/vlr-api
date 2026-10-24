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
    [Theory]
    [InlineData("America/Manaus", "2026-10-01T03:30:00Z", "2026-09-30T23:30:00Z", "2026-09-30", 1)]
    [InlineData("Asia/Tokyo", "2026-09-30T16:30:00Z", "2026-10-01T00:30:00Z", "2026-10-01", 0)]
    public async Task Today_counts_use_the_tenant_civil_date(
        string zoneId, string now, string reservationStart, string civilDate, int loggedInToday)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = h.TenantProvider.TenantId!.Value;
        h.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.Rentals, isActive: true));
        h.Db.TenantSettings.Add(new TenantSetting
        {
            TenantId = tenantId, Key = TenantSettingKeys.BookingTimeZoneId, Value = zoneId,
        });
        var customer = new Customer
        {
            TenantId = tenantId, Name = "TZ", LastLoginAt = DateTimeOffset.Parse(now).AddHours(-5),
        };
        h.Db.Customers.Add(customer);
        AddReservation(h, customer.Id, DateTimeOffset.Parse(reservationStart), ReservationStatus.Confirmed, 80m);
        h.Db.Slots.Add(new Slot
        {
            TenantId = tenantId, RentalAssetId = Guid.NewGuid(), OccupancyKindId = Guid.NewGuid(),
            Date = DateOnly.Parse(civilDate), StartTime = new(10, 0), EndTime = new(11, 0),
            Status = SlotStatus.Available,
        });
        await h.Db.SaveChangesAsync();

        var metrics = await CreateService(h, DateTimeOffset.Parse(now)).GetMetricsAsync(default);

        Assert.Equal(1, metrics.Rentals!.ReservationsTodayConfirmed);
        Assert.Equal(1, metrics.Rentals.SlotsAvailableToday);
        Assert.Equal(loggedInToday, metrics.CustomerActivity.LoggedInToday);
        Assert.Equal(1, metrics.CustomerActivity.LoggedInLast7Days);
        Assert.Equal(1, metrics.CustomerActivity.LoggedInLast30Days);
    }

    [Theory]
    [InlineData("2026-11-01T04:30:00Z", 80)]
    [InlineData("2026-11-01T03:30:00Z", 0)]
    [InlineData("2026-12-01T05:00:00Z", 0)]
    public async Task Havana_month_starts_at_the_earlier_ambiguous_midnight(string start, int revenue)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = h.TenantProvider.TenantId!.Value;
        h.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.Rentals, isActive: true));
        h.Db.TenantSettings.Add(new TenantSetting
        {
            TenantId = tenantId, Key = TenantSettingKeys.BookingTimeZoneId, Value = "America/Havana",
        });
        var customer = new Customer { TenantId = tenantId, Name = "TZ" };
        h.Db.Customers.Add(customer);
        AddReservation(h, customer.Id, DateTimeOffset.Parse(start), ReservationStatus.Confirmed, 80m);
        await h.Db.SaveChangesAsync();

        var metrics = await CreateService(h, DateTimeOffset.Parse("2026-11-15T15:00:00Z"))
            .GetMetricsAsync(default);

        Assert.Equal((decimal)revenue, metrics.Rentals!.ReservedRevenueThisMonth);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("America/Manaus", 80)]
    public async Task Revenue_month_ends_at_tenant_midnight(string? zoneId, int expectedRevenue)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = h.TenantProvider.TenantId!.Value;
        h.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.Rentals, isActive: true));
        if (zoneId is not null)
            h.Db.TenantSettings.Add(new TenantSetting
            {
                TenantId = tenantId, Key = TenantSettingKeys.BookingTimeZoneId, Value = zoneId,
            });
        var customer = new Customer { TenantId = tenantId, Name = "TZ" };
        h.Db.Customers.Add(customer);
        // 23:30 September 30 in Manaus; 00:30 October 1 in Sao Paulo.
        AddReservation(h, customer.Id, new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero),
            ReservationStatus.Confirmed, 80m);
        await h.Db.SaveChangesAsync();
        var metrics = await CreateService(h, new DateTimeOffset(2026, 9, 30, 15, 0, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);
        Assert.Equal((decimal)expectedRevenue, metrics.Rentals!.ReservedRevenueThisMonth);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("America/Manaus", 1)]
    public async Task Civil_month_at_0330_utc_uses_tenant_zone(string? zoneId, int expected)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var tenantId = h.TenantProvider.TenantId!.Value;
        h.Db.TenantModules.Add(new TenantModule(tenantId, PlatformModules.WorkOrders, isActive: true));
        if (zoneId is not null)
            h.Db.TenantSettings.Add(new TenantSetting
            {
                TenantId = tenantId, Key = TenantSettingKeys.BookingTimeZoneId, Value = zoneId,
            });
        var asset = AddAsset(h, "TZ", AssetStatus.Active, requiresMaintenance: false);
        await h.Db.SaveChangesAsync();
        AddWorkOrder(h, asset.Id, new DateOnly(2026, 9, 30), WorkOrderStatus.Pending);
        await h.Db.SaveChangesAsync();
        var result = await CreateService(h, new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero))
            .GetMetricsAsync(CancellationToken.None);
        Assert.Equal(expected, result.WorkOrders!.TotalThisMonth);
    }

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
