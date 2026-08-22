using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalPricingBulkTests
{
    [Fact]
    public async Task ApplyBulk_ReplaceTrue_SingleAsset_CreatesPricings()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var rows = new[] { MorningRow(), EveningRow() };

        var result = await service.ApplyBulkAsync(
            ReplaceRequest(harness.AssetIds, rows),
            CancellationToken.None);

        Assert.Equal(1, result.AppliedAssetCount);
        Assert.Equal(2, result.PricingsCreated);

        var stored = await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None);
        Assert.Equal(2, stored.Count);
        Assert.Contains(stored, p => p.DayOfWeek == DayOfWeek.Monday
            && p.StartTime == new TimeOnly(8, 0)
            && p.EndTime == new TimeOnly(10, 0)
            && p.PricePerHour == 100m);
        Assert.Contains(stored, p => p.DayOfWeek == DayOfWeek.Saturday
            && p.StartTime == new TimeOnly(18, 0)
            && p.EndTime == new TimeOnly(20, 0)
            && p.PricePerHour == 150m);
    }

    [Fact]
    public async Task ApplyBulk_ReplaceTrue_MultipleAssets_AppliesSamePricingsToAll()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 2);
        var service = harness.CreateService();
        var rows = new[] { MorningRow() };

        var result = await service.ApplyBulkAsync(
            ReplaceRequest(harness.AssetIds, rows),
            CancellationToken.None);

        Assert.Equal(2, result.AppliedAssetCount);
        Assert.Equal(2, result.PricingsCreated);

        foreach (var assetId in harness.AssetIds)
        {
            var stored = await service.GetByAssetIdAsync(assetId, CancellationToken.None);
            Assert.Single(stored);
            Assert.Equal(DayOfWeek.Monday, stored[0].DayOfWeek);
            Assert.Equal(new TimeOnly(8, 0), stored[0].StartTime);
            Assert.Equal(new TimeOnly(10, 0), stored[0].EndTime);
            Assert.Equal(100m, stored[0].PricePerHour);
        }
    }

    [Fact]
    public async Task ApplyBulk_ReplaceTrue_WithExisting_ClearsThenInserts()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        await service.CreateAsync(harness.AssetIds[0], ToCreate(MorningRow()), CancellationToken.None);

        var result = await service.ApplyBulkAsync(
            ReplaceRequest(harness.AssetIds, [EveningRow()]),
            CancellationToken.None);

        Assert.Equal(1, result.AppliedAssetCount);
        Assert.Equal(1, result.PricingsCreated);

        var stored = await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal(DayOfWeek.Saturday, stored[0].DayOfWeek);
        Assert.Equal(new TimeOnly(18, 0), stored[0].StartTime);
        Assert.Equal(150m, stored[0].PricePerHour);
    }

    [Fact]
    public async Task ApplyBulk_ReplaceTrue_EmptyPricings_ClearsAll()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        await service.CreateAsync(harness.AssetIds[0], ToCreate(MorningRow()), CancellationToken.None);

        var result = await service.ApplyBulkAsync(
            ReplaceRequest(harness.AssetIds, []),
            CancellationToken.None);

        Assert.Equal(1, result.AppliedAssetCount);
        Assert.Equal(0, result.PricingsCreated);
        Assert.Empty(await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_ReplaceFalse_EmptyPricings_Throws400()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                new BulkApplyPricingsRequest
                {
                    AssetIds = harness.AssetIds,
                    Pricings = [],
                    Replace = false,
                },
                CancellationToken.None));

        Assert.NotNull(ex);
        Assert.Empty(await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_DuplicateAssetIds_Throws400()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var assetId = harness.AssetIds[0];

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest([assetId, assetId], [MorningRow()]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_EmptyAssetIds_Throws400()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest([], [MorningRow()]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_ExceedsMaxAssetIds_Throws400()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var service = new RentalPricingService(db, tenantProvider);
        var assetIds = Enumerable.Range(0, 1001).Select(_ => Guid.NewGuid()).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest(assetIds, [MorningRow()]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_DuplicateExactRows_Throws400()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var duplicate = MorningRow() with { PricePerHour = 200m };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest(harness.AssetIds, [MorningRow(), duplicate]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_InvalidWindow_Throws400()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var invalid = MorningRow() with { EndTime = new TimeOnly(7, 0) };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest(harness.AssetIds, [invalid]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_OverlappingRowsInPayload_Throws409()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var overlapping = MorningRow() with
        {
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(11, 0),
            PricePerHour = 80m,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest(harness.AssetIds, [MorningRow(), overlapping]),
                CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_MissingAsset_Throws404_AndNoPricingsWritten()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        var missingId = Guid.NewGuid();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest([harness.AssetIds[0], missingId], [MorningRow()]),
                CancellationToken.None));

        Assert.Empty(await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None));
    }

    [Fact]
    public async Task ApplyBulk_ReplaceFalse_OverlapWithExisting_Throws409_AndNoNewRows()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 1);
        var service = harness.CreateService();
        await service.CreateAsync(harness.AssetIds[0], ToCreate(MorningRow()), CancellationToken.None);

        var overlapping = MorningRow() with
        {
            StartTime = new TimeOnly(9, 0),
            EndTime = new TimeOnly(11, 0),
            PricePerHour = 80m,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyBulkAsync(
                new BulkApplyPricingsRequest
                {
                    AssetIds = harness.AssetIds,
                    Pricings = [overlapping],
                    Replace = false,
                },
                CancellationToken.None));

        var stored = await service.GetByAssetIdAsync(harness.AssetIds[0], CancellationToken.None);
        Assert.Single(stored);
        Assert.Equal(new TimeOnly(8, 0), stored[0].StartTime);
        Assert.Equal(new TimeOnly(10, 0), stored[0].EndTime);
        Assert.Equal(100m, stored[0].PricePerHour);
    }

    [Fact]
    public async Task ApplyBulk_AssetNotRentable_Throws404()
    {
        await using var harness = await PricingBulkHarness.CreateAsync(rentableCount: 0);
        var assetId = await harness.AddAssetAsync(isRentable: false, rentalActive: true);
        var service = harness.CreateService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ApplyBulkAsync(
                ReplaceRequest([assetId], [MorningRow()]),
                CancellationToken.None));
    }

    private static BulkApplyPricingsRequest ReplaceRequest(
        IReadOnlyList<Guid> assetIds,
        IReadOnlyList<BulkPricingRowDto> pricings) =>
        new()
        {
            AssetIds = assetIds,
            Pricings = pricings,
            Replace = true,
        };

    private static BulkPricingRowDto MorningRow() => new()
    {
        DayOfWeek = DayOfWeek.Monday,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(10, 0),
        PricePerHour = 100m,
        RequiresDeposit = true,
        DepositPercentage = 30m,
    };

    private static BulkPricingRowDto EveningRow() => new()
    {
        DayOfWeek = DayOfWeek.Saturday,
        StartTime = new TimeOnly(18, 0),
        EndTime = new TimeOnly(20, 0),
        PricePerHour = 150m,
        RequiresDeposit = false,
        DepositPercentage = 0m,
    };

    private static CreateRentalPricingDto ToCreate(BulkPricingRowDto row) => new()
    {
        DayOfWeek = row.DayOfWeek,
        StartTime = row.StartTime,
        EndTime = row.EndTime,
        PricePerHour = row.PricePerHour,
        RequiresDeposit = row.RequiresDeposit,
        DepositPercentage = row.DepositPercentage,
    };
}

internal sealed class PricingBulkHarness : IAsyncDisposable
{
    private int _assetOrdinal;

    private PricingBulkHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid tenantId,
        Guid unitId,
        Guid categoryId,
        Guid familyId)
    {
        Db = db;
        TenantProvider = tenantProvider;
        TenantId = tenantId;
        UnitId = unitId;
        CategoryId = categoryId;
        FamilyId = familyId;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Guid TenantId { get; }

    public Guid UnitId { get; }

    public Guid CategoryId { get; }

    public Guid FamilyId { get; }

    public List<Guid> AssetIds { get; } = [];

    public static async Task<PricingBulkHarness> CreateAsync(int rentableCount = 1)
    {
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider);

        var tenant = new Tenant("Clube Precos", "88888888000191", subdomain: "clube-precos");
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = "{}",
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        await db.SaveChangesAsync();

        var harness = new PricingBulkHarness(
            db,
            tenantProvider,
            tenant.Id,
            unit.Id,
            category.Id,
            family.Id);

        for (var i = 0; i < rentableCount; i++)
        {
            await harness.AddAssetAsync(isRentable: true, rentalActive: true);
        }

        return harness;
    }

    public async Task<Guid> AddAssetAsync(bool isRentable, bool rentalActive)
    {
        _assetOrdinal++;
        var asset = new Asset
        {
            TenantId = TenantId,
            UnitId = UnitId,
            CategoryId = CategoryId,
            FamilyId = FamilyId,
            Name = $"Quadra {_assetOrdinal}",
            Tag = $"Q{_assetOrdinal}",
            Status = AssetStatus.Active,
            IsRentable = isRentable,
        };
        var rental = new RentalAsset
        {
            TenantId = TenantId,
            AssetId = asset.Id,
            Type = RentalAssetType.Location,
            TotalQuantity = 1,
            IsActive = rentalActive,
            RequiresDeposit = true,
            SchedulePolicy = SchedulePolicy.OpenHours,
            OpenTime = new TimeOnly(8, 0),
            CloseTime = new TimeOnly(22, 0),
            QueueEnabled = false,
        };

        Db.Assets.Add(asset);
        Db.RentalAssets.Add(rental);
        await Db.SaveChangesAsync();

        if (isRentable && rentalActive)
        {
            AssetIds.Add(asset.Id);
        }

        return asset.Id;
    }

    public RentalPricingService CreateService() => new(Db, TenantProvider);

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
