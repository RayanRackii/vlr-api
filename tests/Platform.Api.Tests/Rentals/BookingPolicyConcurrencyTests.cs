using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingPolicyConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private const int RaceIterations = 4;

    private static readonly DateOnly Day = new(2026, 10, 20);
    private static readonly TimeOnly Start = new(10, 0);
    private static readonly TimeOnly End = new(11, 0);
    private static readonly TimeProvider Clock =
        new TestTimeProvider(BrazilTimeZone.AtLocal(Day, new TimeOnly(8, 0)));

    private readonly PostgresContainerFixture _postgres;

    public BookingPolicyConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Parallel_self_bookings_of_different_resources_allow_only_the_daily_cap()
    {
        var factory = _postgres.Factory;
        Assert.NotNull(factory);

        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            var tenantProvider = new FakeTenantProvider();
            var seed = await SeedTwoResourcesAsync(factory, tenantProvider);

            await using var db1 = factory.Create(tenantProvider);
            await using var db2 = factory.Create(tenantProvider);
            var service1 = CreateService(db1, tenantProvider);
            var service2 = CreateService(db2, tenantProvider);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var captured = await Task.WhenAll(
                CaptureAsync(service1.CreateReservationAsync(
                    seed.CustomerId,
                    Request(seed.UnitId, seed.FirstAssetId),
                    timeout.Token)),
                CaptureAsync(service2.CreateReservationAsync(
                    seed.CustomerId,
                    Request(seed.UnitId, seed.SecondAssetId),
                    timeout.Token)));

            Assert.Equal(1, captured.Count(result => result.Exception is null));
            Assert.Equal(1, captured.Count(result => result.Exception is DailyBookingLimitReachedException));

            await using var verify = factory.Create(tenantProvider);
            Assert.Equal(1, await verify.Reservations.CountAsync(reservation =>
                reservation.CustomerId == seed.CustomerId
                && reservation.Status != ReservationStatus.Canceled));
        }
    }

    private static ReservationService CreateService(AppDbContext db, FakeTenantProvider tenantProvider) =>
        new(
            db,
            tenantProvider,
            new FakeTrialGuard(),
            TestReservationQueue.Create(db, tenantProvider, Clock),
            SilentRentalsNotifications.Publisher,
            SilentRentalsNotifications.Scheduler,
            Clock);

    private static CreateReservationRequestDto Request(Guid unitId, Guid assetId) =>
        new()
        {
            UnitId = unitId,
            Date = Day,
            StartTime = Start,
            EndTime = End,
            Items = [new CreateReservationItemRequestDto { AssetId = assetId, Quantity = 1 }],
        };

    private static async Task<(Exception? Exception, ReservationResponseDto? Response)> CaptureAsync(
        Task<ReservationResponseDto> booking)
    {
        try
        {
            return (null, await booking);
        }
        catch (Exception exception)
        {
            return (exception, null);
        }
    }

    private static async Task<SeededPair> SeedTwoResourcesAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);
        var tenant = new Tenant(
            "Concurrency Club",
            Guid.NewGuid().ToString("N")[..14],
            subdomain: $"lock-{Guid.NewGuid():N}"[..20]);
        var unit = new Unit(tenant.Id, "Main");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Spaces" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };
        var first = CreateAsset(tenant.Id, unit.Id, category.Id, family.Id, "Studio A", "SA");
        var second = CreateAsset(tenant.Id, unit.Id, category.Id, family.Id, "Studio B", "SB");
        var firstRental = CreateRental(tenant.Id, first.Id);
        var secondRental = CreateRental(tenant.Id, second.Id);
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Ada",
            Email = $"ada-{Guid.NewGuid():N}@club.test",
            Phone = $"119{Random.Shared.Next(10000000, 99999999)}",
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.AddRange(first, second);
        db.RentalAssets.AddRange(firstRental, secondRental);
        db.Customers.Add(customer);
        db.BookingPolicies.Add(new BookingPolicy
        {
            TenantId = tenant.Id,
            ResourceType = null,
            MaxActiveReservationsPerCustomerPerDay = 1,
        });
        foreach (var rental in new[] { firstRental, secondRental })
        {
            db.RentalPricings.Add(new RentalPricing
            {
                TenantId = tenant.Id,
                RentalAssetId = rental.Id,
                DayOfWeek = Day.DayOfWeek,
                StartTime = new TimeOnly(8, 0),
                EndTime = new TimeOnly(22, 0),
                PricePerHour = 50m,
                RequiresDeposit = false,
                DepositPercentage = 0m,
            });
        }

        await db.SaveChangesAsync();
        return new SeededPair(unit.Id, customer.Id, first.Id, second.Id);
    }

    private static Asset CreateAsset(
        Guid tenantId,
        Guid unitId,
        Guid categoryId,
        Guid familyId,
        string name,
        string tag) =>
        new()
        {
            TenantId = tenantId,
            UnitId = unitId,
            CategoryId = categoryId,
            FamilyId = familyId,
            Name = name,
            Tag = tag,
            Status = AssetStatus.Active,
            IsRentable = true,
        };

    private static RentalAsset CreateRental(Guid tenantId, Guid assetId) =>
        new()
        {
            TenantId = tenantId,
            AssetId = assetId,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = false,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };

    private sealed record SeededPair(Guid UnitId, Guid CustomerId, Guid FirstAssetId, Guid SecondAssetId);
}
