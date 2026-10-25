using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Assets;

public sealed class AssetRegistryTests
{
    [Fact]
    public async Task CreateCategory_returns_normalized_registry_item()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();

        var created = await registry.CreateCategoryAsync(
            new CreateAssetCategoryRequest { Name = "  Piscina  " },
            CancellationToken.None);

        Assert.Equal("Piscina", created.Name);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Contains(
            harness.Db.AssetCategories,
            category => category.Id == created.Id && category.Name == "Piscina");
    }

    [Fact]
    public async Task CreateCategory_rejects_blank_and_duplicate_names()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.CreateCategoryAsync(
                new CreateAssetCategoryRequest { Name = "   " },
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.CreateCategoryAsync(
                new CreateAssetCategoryRequest { Name = "Quadras" },
                CancellationToken.None));
    }

    [Fact]
    public async Task CreateRentable_creates_asset_and_rental_config_without_inventory_entitlement()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();

        var created = await registry.CreateRentableAsync(
            RentableRequest(harness, name: "Quadra 1", tag: "Q1"),
            CancellationToken.None);

        Assert.True(created.IsRentable);
        Assert.Equal(AssetStatus.Active, created.Status);
        Assert.False(created.RequiresMaintenance);
        Assert.NotNull(created.RentalConfiguration);
        Assert.Equal(RentalAssetType.Location, created.RentalConfiguration.Type);
        Assert.Equal(1, created.RentalConfiguration.TotalQuantity);
        Assert.True(created.RentalConfiguration.IsActive);
        Assert.DoesNotContain(
            harness.Db.TenantModules.IgnoreQueryFilters(),
            module => module.ModuleName == PlatformModules.Inventory);
    }

    [Fact]
    public async Task Inventory_create_still_works_when_inventory_is_on()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        harness.Db.TenantModules.Add(
            new TenantModule(harness.TenantProvider.TenantId!.Value, PlatformModules.Inventory));
        await harness.Db.SaveChangesAsync();
        var service = harness.CreateService();

        var created = await service.CreateAsync(
            new CreateAssetRequest
            {
                UnitId = harness.UnitId,
                CategoryId = harness.CategoryId,
                FamilyId = harness.FamilyId,
                Name = "Elétrico 1",
                Tag = "E1",
                Status = AssetStatus.Active,
                IsRentable = false,
            },
            CancellationToken.None);

        Assert.Equal("E1", created.Tag);
        Assert.False(created.IsRentable);
        Assert.Null(created.RentalConfig);
        Assert.Contains(
            harness.Db.TenantModules.IgnoreQueryFilters(),
            module => module.ModuleName == PlatformModules.Inventory && module.IsActive);
    }

    [Fact]
    public async Task UpdateRentable_updates_existing_rentable_by_rental_asset_id()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            RentableRequest(harness, name: "Quadra 1", tag: "Q1"),
            CancellationToken.None);

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id,
            new UpdateRentableRequest
            {
                Name = "Quadra A",
                Tag = "QA",
                UnitId = harness.UnitId,
                CategoryId = harness.CategoryId,
                FamilyId = harness.FamilyId,
                RentalType = RentalAssetType.Location,
                TotalQuantity = 1,
                RequiresDeposit = false,
                Location = "Bloco A",
            },
            CancellationToken.None);

        Assert.Equal("Quadra A", updated.Name);
        Assert.Equal("QA", updated.Tag);
        Assert.Equal("Bloco A", updated.Location);
        Assert.False(updated.RentalConfiguration!.RequiresDeposit);
        Assert.True(updated.IsRentable);
    }

    // Exact body the web drawer sends (R11 Bot4 repro): no "tag", because
    // RentalAssetResponse does not expose it. Must deserialize, not 400.
    private const string WebRentableBodyWithoutTag = """
        {"name":"Sala Cancelamento","unitId":"66596a2c-82cc-4c0a-bf36-c3c7046a03b5",
         "categoryId":"3ef7d7a1-1610-4770-8980-49f4e88f79b2","familyId":"11111111-1111-1111-1111-111111111101",
         "rentalType":"Location","totalQuantity":1,"requiresDeposit":false,"queueEnabled":false,
         "queueOpeningTime":null,"location":"x"}
        """;

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Web_rentable_body_without_tag_deserializes_for_create_and_update()
    {
        var update = JsonSerializer.Deserialize<UpdateRentableRequest>(WebRentableBodyWithoutTag, WebJson);
        var create = JsonSerializer.Deserialize<CreateRentableRequest>(WebRentableBodyWithoutTag, WebJson);

        Assert.NotNull(update);
        Assert.Null(update.Tag);
        Assert.Equal("x", update.Location);
        Assert.NotNull(create);
        Assert.Null(create.Tag);
    }

    [Fact]
    public async Task UpdateRentable_without_tag_keeps_stored_tag()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            RentableRequest(harness, name: "Sala", tag: "SALA-1"),
            CancellationToken.None);

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id,
            new UpdateRentableRequest
            {
                Name = "Sala",
                UnitId = harness.UnitId,
                CategoryId = harness.CategoryId,
                FamilyId = harness.FamilyId,
                Location = "x",
            },
            CancellationToken.None);

        Assert.Equal("SALA-1", updated.Tag);
        Assert.Equal("x", updated.Location);
    }

    [Fact]
    public async Task CreateRentable_without_tag_generates_distinct_tags()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();
        CreateRentableRequest NoTag(string name) => new()
        {
            Name = name,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
        };

        var first = await registry.CreateRentableAsync(NoTag("A"), CancellationToken.None);
        var second = await registry.CreateRentableAsync(NoTag("B"), CancellationToken.None);

        Assert.StartsWith("RES-", first.Tag);
        Assert.NotEqual(first.Tag, second.Tag);
    }

    [Fact]
    public async Task UpdateRentable_missing_or_not_rentable_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            new CreateRentableRequest
            {
                Name = "Quadra",
                Tag = "Q1",
                UnitId = harness.UnitId,
                CategoryId = harness.CategoryId,
                FamilyId = harness.FamilyId,
                RentalType = RentalAssetType.Location,
                TotalQuantity = 1,
            },
            CancellationToken.None);

        created.IsRentable = false;
        await harness.Db.SaveChangesAsync();

        var rentalId = created.RentalConfiguration!.Id;
        var request = new UpdateRentableRequest
        {
            Name = "X",
            Tag = "X",
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.UpdateRentableAsync(Guid.NewGuid(), request, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.UpdateRentableAsync(rentalId, request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateRentable_foreign_category_unit_or_family_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var foreign = await SeedForeignTenantAsync(harness);
        var registry = harness.CreateRegistry();

        Assert.True(
            await harness.Db.AssetCategories.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == foreign.CategoryId));
        Assert.True(
            await harness.Db.Units.IgnoreQueryFilters()
                .AnyAsync(u => u.Id == foreign.UnitId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.CreateRentableAsync(
                RentableRequest(harness, "Q", "Q-F", categoryId: foreign.CategoryId),
                CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.CreateRentableAsync(
                RentableRequest(harness, "Q", "Q-U", unitId: foreign.UnitId),
                CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.CreateRentableAsync(
                RentableRequest(harness, "Q", "Q-Fam", familyId: Guid.NewGuid()),
                CancellationToken.None));
    }

    [Fact]
    public async Task RequireAsset_foreign_tenant_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var foreign = await SeedForeignTenantAsync(harness);
        var registry = harness.CreateRegistry();

        Assert.True(
            await harness.Db.Assets.IgnoreQueryFilters()
                .AnyAsync(a => a.Id == foreign.AssetId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.RequireAssetAsync(foreign.AssetId, CancellationToken.None));
    }

    [Fact]
    public async Task RequireCategory_foreign_tenant_throws_KeyNotFound()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var foreign = await SeedForeignTenantAsync(harness);
        var registry = harness.CreateRegistry();

        Assert.True(
            await harness.Db.AssetCategories.IgnoreQueryFilters()
                .AnyAsync(c => c.Id == foreign.CategoryId));

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            registry.RequireCategoryAsync(foreign.CategoryId, CancellationToken.None));
    }

    [Fact]
    public async Task List_methods_return_current_tenant_rows_only()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await SeedForeignTenantAsync(harness);
        var registry = harness.CreateRegistry();
        await registry.CreateRentableAsync(
            RentableRequest(harness, "Quadra 1", "Q1"),
            CancellationToken.None);

        var assets = await registry.ListAssetsAsync(CancellationToken.None);
        var categories = await registry.ListCategoriesAsync(CancellationToken.None);
        var families = await registry.ListActiveFamiliesAsync(CancellationToken.None);

        Assert.Single(assets);
        Assert.Equal("Q1", assets[0].Tag);
        Assert.Equal("Quadra 1", assets[0].Name);
        Assert.Equal(harness.UnitId, assets[0].UnitId);
        Assert.Equal(harness.CategoryId, assets[0].CategoryId);
        Assert.Equal(AssetStatus.Active, assets[0].Status);
        Assert.Single(categories);
        Assert.Equal(harness.CategoryId, categories[0].Id);
        Assert.Single(families);
        Assert.Equal(harness.FamilyId, families[0].Id);
    }

    private static CreateRentableRequest RentableRequest(
        BulkCreateAssetsHarness harness,
        string name,
        string tag,
        Guid? unitId = null,
        Guid? categoryId = null,
        Guid? familyId = null) =>
        new()
        {
            Name = name,
            Tag = tag,
            UnitId = unitId ?? harness.UnitId,
            CategoryId = categoryId ?? harness.CategoryId,
            FamilyId = familyId ?? harness.FamilyId,
            RentalType = RentalAssetType.Location,
            TotalQuantity = 1,
        };

    private static async Task<ForeignSeed> SeedForeignTenantAsync(BulkCreateAssetsHarness harness)
    {
        var other = new Tenant("Other Club", "88888888000191", subdomain: "other-club");
        var unit = new Unit(other.Id, "Filial");
        var category = new AssetCategory { TenantId = other.Id, Name = "Foreign Quadras" };
        var family = new AssetFamily
        {
            Key = $"foreign-{Guid.NewGuid():N}"[..32],
            Label = "Foreign",
            FieldSchemaJson = """{"fields":[]}""",
        };
        harness.Db.Tenants.Add(other);
        harness.Db.Units.Add(unit);
        harness.Db.AssetCategories.Add(category);
        harness.Db.AssetFamilies.Add(family);
        await harness.Db.SaveChangesAsync();

        var asset = new Asset
        {
            TenantId = other.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Foreign court",
            Tag = "FX1",
            Status = AssetStatus.Active,
        };
        harness.Db.Assets.Add(asset);
        await harness.Db.SaveChangesAsync();

        return new ForeignSeed(other.Id, unit.Id, category.Id, family.Id, asset.Id);
    }

    private sealed record ForeignSeed(
        Guid TenantId,
        Guid UnitId,
        Guid CategoryId,
        Guid FamilyId,
        Guid AssetId);
}
