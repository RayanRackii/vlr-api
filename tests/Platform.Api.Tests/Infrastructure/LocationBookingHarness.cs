using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

internal sealed class LocationBookingHarness : IAsyncDisposable
{
    public static readonly DateOnly Date = new(2026, 9, 1);
    public static readonly TimeOnly Start = new(10, 0);
    public static readonly TimeOnly End = new(11, 0);
    public const string AssetName = "Quadra 1";

    private LocationBookingHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid tenantId,
        Guid unitId,
        Guid customerId,
        Guid assetId,
        Guid rentalAssetId)
    {
        Db = db;
        TenantProvider = tenantProvider;
        TenantId = tenantId;
        UnitId = unitId;
        CustomerId = customerId;
        AssetId = assetId;
        RentalAssetId = rentalAssetId;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Guid TenantId { get; }

    public Guid UnitId { get; }

    public Guid CustomerId { get; }

    public Guid AssetId { get; }

    public Guid RentalAssetId { get; }

    public static DateTimeOffset RangeStart =>
        new(Date.ToDateTime(Start, DateTimeKind.Unspecified), TimeSpan.Zero);

    public static DateTimeOffset RangeEnd => RangeStart.AddHours(1);

    public static async Task<LocationBookingHarness> CreateAsync()
    {
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider);

        var tenant = new Tenant("Clube Reserva", "99999999000191", subdomain: "clube-reserva");
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
            Name = AssetName,
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

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        db.RentalAssets.Add(rental);
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        return new LocationBookingHarness(
            db,
            tenantProvider,
            tenant.Id,
            unit.Id,
            customer.Id,
            asset.Id,
            rental.Id);
    }

    public void SeedOverlappingReservation(ReservationStatus status)
    {
        var reservation = new Reservation
        {
            TenantId = TenantId,
            UnitId = UnitId,
            CustomerId = CustomerId,
            CustomerName = "Existing",
            CustomerWhatsApp = "11999999999",
            StartDateTime = RangeStart,
            EndDateTime = RangeEnd,
            Status = status,
            TotalAmount = 100m,
            DepositPaid = 0m,
        };
        reservation.AddItem(new ReservationItem
        {
            TenantId = TenantId,
            ReservationId = reservation.Id,
            RentalAssetId = RentalAssetId,
            Quantity = 1,
            UnitPrice = 100m,
            SubTotal = 100m,
        });
        Db.Reservations.Add(reservation);
    }

    public ReservationService CreateReservationService() =>
        new(Db, TenantProvider, new FakeTrialGuard());

    public ScheduleService CreateScheduleService() =>
        new(Db, TenantProvider, new UnusedOccupancyKindService(), new FakeTrialGuard());

    public ValueTask DisposeAsync() => Db.DisposeAsync();

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
