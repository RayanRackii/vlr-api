using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Assets;

public sealed class AssetServiceBulkCreateTests
{
    [Fact]
    public async Task BulkCreate_LocationRange1To6_CreatesSixAssetsWithQuantityOne()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var result = await service.BulkCreateAsync(
            LocationRequest(harness, start: 1, end: 6),
            CancellationToken.None);

        Assert.Equal(6, result.CreatedCount);
        Assert.Equal(
            ["Q-1", "Q-2", "Q-3", "Q-4", "Q-5", "Q-6"],
            result.Assets.Select(a => a.Tag).ToList());
        Assert.All(result.Assets, asset =>
        {
            Assert.NotNull(asset.RentalConfig);
            Assert.Equal(RentalAssetType.Location, asset.RentalConfig.Type);
            Assert.Equal(1, asset.RentalConfig.TotalQuantity);
        });
        Assert.Equal(6, harness.Db.Assets.Count());
    }

    [Fact]
    public async Task BulkCreate_GoodQuantity100_CreatesOneAssetWithStock100()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var result = await service.BulkCreateAsync(
            GoodRequest(harness, totalQuantity: 100),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCount);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("RAQ", asset.Tag);
        Assert.NotNull(asset.RentalConfig);
        Assert.Equal(RentalAssetType.Good, asset.RentalConfig.Type);
        Assert.Equal(100, asset.RentalConfig.TotalQuantity);
        Assert.Equal(1, harness.Db.Assets.Count());
    }

    [Fact]
    public async Task BulkCreate_GoodQuantity1_CreatesOneAssetWithStock1()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var result = await service.BulkCreateAsync(
            GoodRequest(harness, totalQuantity: 1),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCount);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("RAQ", asset.Tag);
        Assert.NotNull(asset.RentalConfig);
        Assert.Equal(RentalAssetType.Good, asset.RentalConfig.Type);
        Assert.Equal(1, asset.RentalConfig.TotalQuantity);
        Assert.Equal(1, harness.Db.Assets.Count());
    }

    [Fact]
    public async Task BulkCreate_RentalTypeOmittedWithStartEnd_CreatesLocations()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var result = await service.BulkCreateAsync(
            new BulkCreateAssetsRequest
            {
                UnitId = harness.UnitId,
                CategoryId = harness.CategoryId,
                FamilyId = harness.FamilyId,
                BaseLocationName = "Quadra",
                BaseTag = "Q-",
                StartNumber = 1,
                EndNumber = 2,
                IsRentable = true,
            },
            CancellationToken.None);

        Assert.Equal(2, result.CreatedCount);
        Assert.All(result.Assets, asset =>
        {
            Assert.NotNull(asset.RentalConfig);
            Assert.Equal(RentalAssetType.Location, asset.RentalConfig.Type);
            Assert.Equal(1, asset.RentalConfig.TotalQuantity);
        });
    }

    [Fact]
    public async Task BulkCreate_GoodWithStartEnd_DoesNotCreateLocations()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var result = await service.BulkCreateAsync(
            GoodRequest(harness, totalQuantity: 100, start: 1, end: 6),
            CancellationToken.None);

        Assert.Equal(1, result.CreatedCount);
        var asset = Assert.Single(result.Assets);
        Assert.Equal("RAQ", asset.Tag);
        Assert.DoesNotContain('-', asset.Tag);
        Assert.NotNull(asset.RentalConfig);
        Assert.Equal(RentalAssetType.Good, asset.RentalConfig.Type);
        Assert.Equal(100, asset.RentalConfig.TotalQuantity);
        Assert.Equal(1, harness.Db.Assets.Count());
        Assert.Equal(RentalAssetType.Good, harness.Db.RentalAssets.Single().Type);
    }

    [Fact]
    public async Task BulkCreate_LocationMissingStartEnd_ThrowsArgumentException()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BulkCreateAsync(
                new BulkCreateAssetsRequest
                {
                    UnitId = harness.UnitId,
                    CategoryId = harness.CategoryId,
                    FamilyId = harness.FamilyId,
                    BaseLocationName = "Quadra",
                    BaseTag = "Q-",
                    RentalType = RentalAssetType.Location,
                    IsRentable = true,
                },
                CancellationToken.None));

        Assert.Empty(harness.Db.Assets);
    }

    [Fact]
    public async Task BulkCreate_LocationRangeExceedsMax_ThrowsArgumentException()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BulkCreateAsync(
                LocationRequest(harness, start: 1, end: 1001),
                CancellationToken.None));

        Assert.Equal("Bulk create is limited to 1000 assets per request.", ex.Message);
        Assert.Empty(harness.Db.Assets);
    }

    [Fact]
    public async Task BulkCreate_GoodTotalQuantityZero_ThrowsArgumentException()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var service = harness.CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.BulkCreateAsync(
                GoodRequest(harness, totalQuantity: 0),
                CancellationToken.None));

        Assert.Empty(harness.Db.Assets);
    }

    private static BulkCreateAssetsRequest LocationRequest(
        BulkCreateAssetsHarness harness,
        int start,
        int end) =>
        new()
        {
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            BaseLocationName = "Quadra",
            BaseTag = "Q-",
            StartNumber = start,
            EndNumber = end,
            RentalType = RentalAssetType.Location,
            IsRentable = true,
        };

    private static BulkCreateAssetsRequest GoodRequest(
        BulkCreateAssetsHarness harness,
        int totalQuantity,
        int? start = null,
        int? end = null) =>
        new()
        {
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            BaseLocationName = "Estoque",
            BaseTag = "RAQ",
            StartNumber = start,
            EndNumber = end,
            RentalType = RentalAssetType.Good,
            TotalQuantity = totalQuantity,
            IsRentable = true,
        };
}

internal sealed class BulkCreateAssetsHarness : IAsyncDisposable
{
    private BulkCreateAssetsHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Guid unitId,
        Guid categoryId,
        Guid familyId)
    {
        Db = db;
        TenantProvider = tenantProvider;
        UnitId = unitId;
        CategoryId = categoryId;
        FamilyId = familyId;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Guid UnitId { get; }

    public Guid CategoryId { get; }

    public Guid FamilyId { get; }

    public static async Task<BulkCreateAssetsHarness> CreateAsync()
    {
        var tenantProvider = new FakeTenantProvider();
        var db = InMemoryAppDb.Create(tenantProvider);

        var tenant = new Tenant("Clube Bulk", "77777777000191", subdomain: "clube-bulk");
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var family = new AssetFamily
        {
            Key = $"spaces-{Guid.NewGuid():N}"[..32],
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
        };

        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, family.Id));
        await db.SaveChangesAsync();

        return new BulkCreateAssetsHarness(db, tenantProvider, unit.Id, category.Id, family.Id);
    }

    public AssetService CreateService() =>
        new(Db, TenantProvider, new FakeTrialGuard());

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
