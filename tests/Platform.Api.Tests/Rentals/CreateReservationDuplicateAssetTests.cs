using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class CreateReservationDuplicateAssetTests
{
    [Fact]
    public async Task CreateReservationAsync_distinct_asset_ids_succeeds()
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        var service = harness.CreateReservationService();
        var request = harness.CreateRequest(harness.AssetIds[0], harness.AssetIds[1]);

        var created = await service.CreateReservationAsync(
            harness.CustomerId,
            request,
            CancellationToken.None);

        Assert.Equal(2, created.Items.Count);
        Assert.Equal(1, await harness.Db.Reservations.CountAsync());
        Assert.Equal(2, await harness.Db.ReservationItems.CountAsync());
    }

    [Fact]
    public async Task CreateReservationAsync_repeated_asset_id_throws_argument_exception()
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        var service = harness.CreateReservationService();
        var request = harness.CreateRequest(harness.AssetIds[0], harness.AssetIds[0]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(harness.CustomerId, request, CancellationToken.None));

        Assert.True(
            ex.Message.Contains("once", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("assetId", StringComparison.OrdinalIgnoreCase),
            ex.Message);
    }

    [Fact]
    public async Task CreateReservationAsync_repeated_asset_id_does_not_persist()
    {
        await using var harness = await TwoLocationBookingHarness.CreateAsync();
        var service = harness.CreateReservationService();
        var request = harness.CreateRequest(harness.AssetIds[0], harness.AssetIds[0]);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(harness.CustomerId, request, CancellationToken.None));

        Assert.Equal(0, await harness.Db.Reservations.CountAsync());
        Assert.Equal(0, await harness.Db.ReservationItems.CountAsync());
    }
}

internal sealed class TwoLocationBookingHarness : IAsyncDisposable
{
    private TwoLocationBookingHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid tenantId,
        Guid unitId,
        Guid customerId,
        IReadOnlyList<Guid> assetIds,
        IReadOnlyList<Guid> rentalAssetIds)
    {
        Db = db;
        TenantProvider = tenantProvider;
        TenantId = tenantId;
        UnitId = unitId;
        CustomerId = customerId;
        AssetIds = assetIds;
        RentalAssetIds = rentalAssetIds;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Guid TenantId { get; }

    public Guid UnitId { get; }

    public Guid CustomerId { get; }

    public IReadOnlyList<Guid> AssetIds { get; }

    public IReadOnlyList<Guid> RentalAssetIds { get; }

    public static async Task<TwoLocationBookingHarness> CreateAsync()
    {
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider);

        var tenant = new Tenant("Clube Reserva Dup", "77777777000191", subdomain: "clube-reserva-dup");
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente B2C",
            Email = "cliente@club.test",
            Phone = "11999999999",
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Customers.Add(customer);

        var assetIds = new List<Guid>(2);
        var rentalAssetIds = new List<Guid>(2);

        for (var i = 1; i <= 2; i++)
        {
            var asset = new Asset
            {
                TenantId = tenant.Id,
                UnitId = unit.Id,
                CategoryId = category.Id,
                FamilyId = family.Id,
                Name = $"Quadra {i}",
                Tag = $"Q{i}",
                Status = AssetStatus.Active,
                IsRentable = true,
            };
            var rental = new RentalAsset
            {
                TenantId = tenant.Id,
                AssetId = asset.Id,
                Type = RentalAssetType.Location,
                TotalQuantity = 1,
                IsActive = true,
                RequiresDeposit = true,
                SchedulePolicy = SchedulePolicy.OpenHours,
                OpenTime = new TimeOnly(8, 0),
                CloseTime = new TimeOnly(22, 0),
                QueueEnabled = false,
            };
            var pricing = new RentalPricing
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = LocationBookingHarness.Date.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(22, 0),
                PricePerHour = 100m,
                RequiresDeposit = true,
                DepositPercentage = 50m,
            };

            db.Assets.Add(asset);
            db.RentalAssets.Add(rental);
            db.RentalPricings.Add(pricing);
            assetIds.Add(asset.Id);
            rentalAssetIds.Add(rental.Id);
        }

        await db.SaveChangesAsync();

        return new TwoLocationBookingHarness(
            db,
            tenantProvider,
            tenant.Id,
            unit.Id,
            customer.Id,
            assetIds,
            rentalAssetIds);
    }

    public CreateReservationRequestDto CreateRequest(params Guid[] assetIds) =>
        new()
        {
            UnitId = UnitId,
            Date = LocationBookingHarness.Date,
            StartTime = LocationBookingHarness.Start,
            EndTime = LocationBookingHarness.End,
            Items = assetIds
                .Select(assetId => new CreateReservationItemRequestDto { AssetId = assetId, Quantity = 1 })
                .ToList(),
        };

    public ReservationService CreateReservationService() =>
        new(Db, TenantProvider, new FakeTrialGuard(), TestReservationQueue.Create(Db, TenantProvider));

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
