using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Infrastructure;

internal sealed class ReservationQueueHarness : IAsyncDisposable
{
    public static readonly TimeOnly OpeningTime = new(7, 30);
    public static readonly DateOnly OpeningDate = new(2026, 8, 22);
    public static readonly DateOnly BookDate = new(2026, 8, 22);
    public static readonly TimeOnly BookStart = new(10, 0);
    public static readonly TimeOnly BookEnd = new(11, 0);

    private ReservationQueueHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        TestTimeProvider time,
        Guid tenantId,
        Guid unitId,
        Guid assetId,
        Guid rentalAssetId,
        IReadOnlyList<Guid> customerIds,
        Guid? slotId,
        bool ownsDb)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Time = time;
        TenantId = tenantId;
        UnitId = unitId;
        AssetId = assetId;
        RentalAssetId = rentalAssetId;
        CustomerIds = customerIds;
        SlotId = slotId;
        _ownsDb = ownsDb;
    }

    private readonly bool _ownsDb;

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public TestTimeProvider Time { get; }

    public Guid TenantId { get; }

    public Guid UnitId { get; }

    public Guid AssetId { get; }

    public Guid RentalAssetId { get; }

    public IReadOnlyList<Guid> CustomerIds { get; }

    public Guid? SlotId { get; }

    public Guid CustomerA => CustomerIds[0];

    public Guid CustomerB => CustomerIds[1];

    public Guid CustomerC => CustomerIds[2];

    public static DateTimeOffset Brazil(int hour, int minute, int second = 0) =>
        Brazil(OpeningDate, hour, minute, second);

    public static DateTimeOffset Brazil(DateOnly date, int hour, int minute, int second = 0)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute, second), DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, BrazilTimeZone.Resolve());
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static async Task<ReservationQueueHarness> CreateAsync(
        bool queueEnabled = true,
        int customerCount = 3,
        bool includeSlot = false,
        string? databaseName = null,
        FakeTenantProvider? tenantProvider = null,
        RentalAssetType type = RentalAssetType.Location)
    {
        tenantProvider ??= new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider, databaseName);
        var time = new TestTimeProvider(Brazil(7, 10));

        var tenant = new Tenant(
            "Clube Fila",
            UniqueTaxId(),
            subdomain: $"fila-{Guid.NewGuid():N}"[..20]);
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
            Type = type,
            TotalQuantity = 1,
            IsActive = true,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = queueEnabled && type == RentalAssetType.Location,
            QueueOpeningTime = queueEnabled && type == RentalAssetType.Location ? OpeningTime : null,
        };
        var pricing = new RentalPricing
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = BookDate.DayOfWeek,
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
        db.RentalPricings.Add(pricing);

        var customerIds = new List<Guid>(customerCount);
        for (var i = 0; i < customerCount; i++)
        {
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = $"Cliente {i + 1}",
                Email = $"c{i}-{Guid.NewGuid():N}@club.test",
                Phone = $"1199999{i:0000}",
            };
            db.Customers.Add(customer);
            customerIds.Add(customer.Id);
        }

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
                Date = BookDate,
                StartTime = BookStart,
                EndTime = BookEnd,
                OccupancyKindId = kind.Id,
                Status = SlotStatus.Available,
            };
            db.OccupancyKinds.Add(kind);
            db.Slots.Add(slot);
            slotId = slot.Id;
        }

        await db.SaveChangesAsync();

        return new ReservationQueueHarness(
            db,
            tenantProvider,
            time,
            tenant.Id,
            unit.Id,
            asset.Id,
            rental.Id,
            customerIds,
            slotId,
            ownsDb: true);
    }

    public static Task<ReservationQueueHarness> CreateOnAsync(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        TestTimeProvider time,
        bool queueEnabled = true,
        int customerCount = 3,
        bool includeSlot = false) =>
        SeedOntoAsync(db, tenantProvider, time, queueEnabled, customerCount, includeSlot);

    public ReservationQueueService Queue() =>
        TestReservationQueue.Create(Db, TenantProvider, Time);

    public ReservationService Reservations() =>
        new(Db, TenantProvider, new FakeTrialGuard(), Queue());

    public ScheduleService Schedule() =>
        new(Db, TenantProvider, new UnusedOccupancyKindService(), new FakeTrialGuard(), Queue());

    public Platform.Api.Modules.Rentals.Dtos.CreateReservationRequestDto BookRequest() =>
        new()
        {
            UnitId = UnitId,
            Date = BookDate,
            StartTime = BookStart,
            EndTime = BookEnd,
            Items =
            [
                new Platform.Api.Modules.Rentals.Dtos.CreateReservationItemRequestDto
                {
                    AssetId = AssetId,
                    Quantity = 1
                }
            ]
        };

    public ValueTask DisposeAsync() =>
        _ownsDb ? Db.DisposeAsync() : ValueTask.CompletedTask;

    public static string UniqueTaxId() => Guid.NewGuid().ToString("N")[..14];

    private static async Task<ReservationQueueHarness> SeedOntoAsync(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        TestTimeProvider time,
        bool queueEnabled,
        int customerCount,
        bool includeSlot)
    {
        var tenant = new Tenant(
            "Clube Fila Pg",
            UniqueTaxId(),
            subdomain: $"filapg-{Guid.NewGuid():N}"[..20]);
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
            QueueEnabled = queueEnabled,
            QueueOpeningTime = queueEnabled ? OpeningTime : null,
        };
        var pricing = new RentalPricing
        {
            TenantId = tenant.Id,
            RentalAssetId = rental.Id,
            DayOfWeek = BookDate.DayOfWeek,
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
        db.RentalPricings.Add(pricing);

        var customerIds = new List<Guid>(customerCount);
        for (var i = 0; i < customerCount; i++)
        {
            var customer = new Customer
            {
                TenantId = tenant.Id,
                Name = $"Cliente {i + 1}",
                Email = $"c{i}-{Guid.NewGuid():N}@club.test",
                Phone = $"1198888{i:0000}",
            };
            db.Customers.Add(customer);
            customerIds.Add(customer.Id);
        }

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
                Date = BookDate,
                StartTime = BookStart,
                EndTime = BookEnd,
                OccupancyKindId = kind.Id,
                Status = SlotStatus.Available,
            };
            db.OccupancyKinds.Add(kind);
            db.Slots.Add(slot);
            slotId = slot.Id;
        }

        await db.SaveChangesAsync();

        return new ReservationQueueHarness(
            db,
            tenantProvider,
            time,
            tenant.Id,
            unit.Id,
            asset.Id,
            rental.Id,
            customerIds,
            slotId,
            ownsDb: false);
    }

    private sealed class UnusedOccupancyKindService : Platform.Api.Modules.Rentals.Services.IOccupancyKindService
    {
        public Task<IReadOnlyList<Platform.Api.Modules.Rentals.Dtos.OccupancyKindResponseDto>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Platform.Api.Modules.Rentals.Dtos.OccupancyKindResponseDto> CreateAsync(
            Platform.Api.Modules.Rentals.Dtos.UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Platform.Api.Modules.Rentals.Dtos.OccupancyKindResponseDto> UpdateAsync(
            Guid id,
            Platform.Api.Modules.Rentals.Dtos.UpsertOccupancyKindRequestDto request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EnsureDefaultsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
