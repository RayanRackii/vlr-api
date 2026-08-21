using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class ReservationConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly DateOnly Date = new(2026, 9, 1);
    private static readonly TimeOnly Start = new(10, 0);
    private static readonly TimeOnly End = new(11, 0);

    private readonly PostgresContainerFixture _postgres;

    public ReservationConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task CreateReservationAsync_parallel_same_location_allows_only_one_blocking()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedLocationAsync(factory, tenantProvider, includeSlot: false);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var service1 = new ReservationService(db1, tenantProvider, new FakeTrialGuard());
        var service2 = new ReservationService(db2, tenantProvider, new FakeTrialGuard());

        var request = new CreateReservationRequestDto
        {
            UnitId = seed.UnitId,
            Date = Date,
            StartTime = Start,
            EndTime = End,
            Items =
            [
                new CreateReservationItemRequestDto { AssetId = seed.AssetId, Quantity = 1 }
            ]
        };

        var captured = await Task.WhenAll(
            CaptureAsync(service1.CreateReservationAsync(seed.CustomerId, request, CancellationToken.None)),
            CaptureAsync(service2.CreateReservationAsync(seed.CustomerId, request, CancellationToken.None)));

        AssertSingleWinner(captured);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
    }

    [DockerFact]
    public async Task BookSlotAsync_parallel_same_location_allows_only_one_blocking()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedLocationAsync(factory, tenantProvider, includeSlot: true);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var occupancyKinds = new UnusedOccupancyKindService();
        var service1 = new ScheduleService(db1, tenantProvider, occupancyKinds, new FakeTrialGuard());
        var service2 = new ScheduleService(db2, tenantProvider, occupancyKinds, new FakeTrialGuard());

        var request = new BookSlotRequestDto
        {
            SlotId = seed.SlotId!.Value,
            UnitId = seed.UnitId,
            Quantity = 1
        };

        var captured = await Task.WhenAll(
            CaptureAsync(service1.BookSlotAsync(seed.CustomerId, request, CancellationToken.None)),
            CaptureAsync(service2.BookSlotAsync(seed.CustomerId, request, CancellationToken.None)));

        AssertSingleWinner(captured);

        await using var verify = factory.Create(tenantProvider);
        Assert.Equal(1, await CountBlockingAsync(verify));
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static async Task<SeededLocation> SeedLocationAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        bool includeSlot)
    {
        await using var db = factory.Create(tenantProvider);

        var tenant = new Tenant("Clube Concurrency", UniqueTaxId(), subdomain: $"lock-{Guid.NewGuid():N}"[..20]);
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra 1",
            Tag = "Q1",
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
        };
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Cliente B2C",
            Email = "cliente@club.test",
            Phone = "11999999999",
        };
        var pricing = new RentalPricing
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = Date.DayOfWeek,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(22, 0),
            PricePerHour = 100m,
            RequiresDeposit = true,
            DepositPercentage = 50m,
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        db.RentalPricings.Add(pricing);

        Guid? slotId = null;
        if (includeSlot)
        {
            var kind = new OccupancyKind
            {
                TenantId = tenant.Id,
                Key = "open",
                Label = "Aberto",
                IsBookableByCustomer = true,
                BlocksCapacity = true,
                SortOrder = 0,
                IsActive = true,
            };
            var slot = new Slot
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                Date = Date,
                StartTime = Start,
                EndTime = End,
                OccupancyKindId = kind.Id,
                Status = SlotStatus.Available,
            };
            db.OccupancyKinds.Add(kind);
            db.Slots.Add(slot);
            slotId = slot.Id;
        }

        await db.SaveChangesAsync();

        return new SeededLocation(tenant.Id, unit.Id, customer.Id, asset.Id, rental.Id, slotId);
    }

    private static void AssertSingleWinner(Exception?[] captured)
    {
        var successes = captured.Count(ex => ex is null);
        var failure = Assert.Single(captured, ex => ex is not null);
        Assert.Equal(1, successes);

        var ioe = Assert.IsType<InvalidOperationException>(failure);
        Assert.True(
            ioe.Message.Contains("not available", StringComparison.OrdinalIgnoreCase)
            || ioe.Message.Contains("already reserved", StringComparison.OrdinalIgnoreCase),
            ioe.Message);
    }

    private static Task<int> CountBlockingAsync(AppDbContext db) =>
        db.Reservations.CountAsync(r =>
            r.Status == ReservationStatus.PendingDeposit
            || r.Status == ReservationStatus.Confirmed);

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string UniqueTaxId()
    {
        var digits = Guid.NewGuid().ToString("N")[..14];
        return digits;
    }

    private sealed record SeededLocation(
        Guid TenantId,
        Guid UnitId,
        Guid CustomerId,
        Guid AssetId,
        Guid RentalAssetId,
        Guid? SlotId);

    private sealed class UnusedOccupancyKindService : IOccupancyKindService
    {
        public Task<IReadOnlyList<OccupancyKindResponseDto>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> CreateAsync(
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OccupancyKindResponseDto> UpdateAsync(
            Guid id,
            UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
