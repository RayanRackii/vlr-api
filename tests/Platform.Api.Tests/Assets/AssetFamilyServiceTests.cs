using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Assets;

public sealed class AssetFamilyServiceTests
{
    [Fact]
    public async Task ListCatalogAsync_returns_active_global_families_without_tenant_or_join_rows()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);

        var active = new AssetFamily
        {
            Key = "spaces",
            Label = "Spaces",
            FieldSchemaJson = """{"fields":[]}""",
            SortOrder = 1,
            IsActive = true,
        };
        var inactive = new AssetFamily
        {
            Key = "electrical",
            Label = "Electrical",
            FieldSchemaJson = """{"fields":[]}""",
            SortOrder = 2,
            IsActive = false,
        };
        var tenant = new Tenant("Clube Catalog", "12345678000199", subdomain: "clube-catalog");

        db.AssetFamilies.AddRange(active, inactive);
        db.Tenants.Add(tenant);
        db.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, active.Id));
        db.TenantAssetFamilies.Add(new TenantAssetFamily(tenant.Id, inactive.Id));
        await db.SaveChangesAsync();

        var service = new AssetFamilyService(db);
        var catalog = await service.ListCatalogAsync(CancellationToken.None);

        var item = Assert.Single(catalog);
        Assert.Equal(active.Id, item.Id);
        Assert.Equal("spaces", item.Key);
        Assert.Equal("Spaces", item.Label);
        Assert.Equal(1, item.SortOrder);
        Assert.True(item.IsActive);
        Assert.Empty(item.Fields);
    }

    [Fact]
    public async Task CreateAsync_duplicate_key_returns_conflict()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var service = new AssetFamilyService(db);

        await service.CreateAsync(Family("courts", "Courts", isActive: true), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateAsync(Family(" Courts ", "Other", isActive: true), CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_rejects_invalid_key_and_field_schema()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var service = new AssetFamilyService(db);

        var key = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Family("1court", "Courts", isActive: true), CancellationToken.None));
        Assert.Contains("Key", key.Message, StringComparison.Ordinal);

        var duplicated = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(
                new CreateAssetFamilyRequest(
                    "courts",
                    "Courts",
                    [
                        new AssetFamilyFieldDto("surface", "text", false, null),
                        new AssetFamilyFieldDto(" surface ", "number", false, null),
                    ],
                    1,
                    true),
                CancellationToken.None));
        Assert.Contains("duplicated", duplicated.Message, StringComparison.OrdinalIgnoreCase);

        var type = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(
                new CreateAssetFamilyRequest(
                    "courts",
                    "Courts",
                    [new AssetFamilyFieldDto("surface", "date", false, null)],
                    1,
                    true),
                CancellationToken.None));
        Assert.Contains("text, number, or boolean", type.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateAsync_rejects_a_different_key()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var service = new AssetFamilyService(db);
        var created = await service.CreateAsync(
            Family("courts", "Courts", isActive: true),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                created.Id,
                Update(created, label: "Courts", isActive: true, key: "pitches"),
                CancellationToken.None));

        Assert.Contains("cannot be changed", ex.Message, StringComparison.OrdinalIgnoreCase);

        var sameKey = await service.UpdateAsync(
            created.Id,
            Update(created, label: "Quadras", isActive: true, key: "COURTS"),
            CancellationToken.None);

        Assert.Equal("courts", sameKey.Key);
        Assert.Equal("Quadras", sameKey.Label);
    }

    [Fact]
    public async Task Seed_families_cannot_be_deleted_or_deactivated_but_label_sort_and_schema_can_change()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Clube Seed", "12345678000191", subdomain: "clube-seed");
        var unit = new Unit(tenant.Id, "Matriz");
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Quadras" };
        var spaces = new AssetFamily
        {
            Key = AssetFamilyKeys.Spaces,
            Label = "Espaços",
            FieldSchemaJson = """{"fields":[{"key":"capacity","type":"number","required":false,"label":"Capacidade"}]}""",
            SortOrder = 10,
            IsActive = true,
        };
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = spaces.Id,
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
            Attributes = new Dictionary<string, string?> { ["capacity"] = "12" },
        };

        db.Tenants.Add(tenant);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(spaces);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var service = new AssetFamilyService(db);
        var deactivate = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                spaces.Id,
                new UpdateAssetFamilyRequest("Espaços", [], 10, false),
                CancellationToken.None));
        Assert.Contains("deactivated", deactivate.Message, StringComparison.OrdinalIgnoreCase);

        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(spaces.Id, CancellationToken.None));
        Assert.Contains("Seed", delete.Message, StringComparison.Ordinal);

        var updated = await service.UpdateAsync(
            spaces.Id,
            new UpdateAssetFamilyRequest(
                "Quadras",
                [new AssetFamilyFieldDto("surface", "text", true, "Piso")],
                12,
                true),
            CancellationToken.None);

        Assert.Equal(AssetFamilyKeys.Spaces, updated.Key);
        Assert.Equal("Quadras", updated.Label);
        Assert.Equal(12, updated.SortOrder);
        Assert.True(updated.IsActive);
        var field = Assert.Single(updated.Fields);
        Assert.Equal("surface", field.Key);
        Assert.Equal("text", field.Type);

        var stored = await db.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == spaces.Id);
        Assert.Equal(
            """{"fields":[{"key":"surface","type":"text","required":true,"label":"Piso"}]}""",
            stored.FieldSchemaJson);
        Assert.Equal("12", asset.Attributes["capacity"]);
        Assert.Single(asset.Attributes);
        Assert.Equal(EntityState.Unchanged, db.Entry(asset).State);
    }

    [Fact]
    public async Task DeleteAsync_is_blocked_by_an_asset_in_another_tenant()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var owner = new Tenant("Owner", "12345678000192", subdomain: "owner-family");
        var ambient = new Tenant("Ambient", "12345678000193", subdomain: "ambient-family");
        var unit = new Unit(owner.Id, "Matriz");
        var category = new AssetCategory { TenantId = owner.Id, Name = "Quadras" };
        var family = CustomFamily("courts");
        var asset = new Asset
        {
            TenantId = owner.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = "Quadra 1",
            Tag = "Q1",
            Status = AssetStatus.Active,
        };

        db.Tenants.AddRange(owner, ambient);
        db.Units.Add(unit);
        db.AssetCategories.Add(category);
        db.AssetFamilies.Add(family);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        tenantProvider.TenantId = ambient.Id;
        var service = new AssetFamilyService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(family.Id, CancellationToken.None));

        Assert.Contains("in use", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await db.AssetFamilies.SingleOrDefaultAsync(f => f.Id == family.Id));
    }

    [Fact]
    public async Task DeleteAsync_is_blocked_by_a_tenant_link_in_another_tenant()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var owner = new Tenant("Owner Link", "12345678000194", subdomain: "owner-link");
        var ambient = new Tenant("Ambient Link", "12345678000195", subdomain: "ambient-link");
        var family = CustomFamily("lighting");

        db.Tenants.AddRange(owner, ambient);
        db.AssetFamilies.Add(family);
        db.TenantAssetFamilies.Add(new TenantAssetFamily(owner.Id, family.Id));
        await db.SaveChangesAsync();

        tenantProvider.TenantId = ambient.Id;
        var service = new AssetFamilyService(db);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DeleteAsync(family.Id, CancellationToken.None));

        Assert.Contains("in use", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await db.AssetFamilies.IgnoreQueryFilters().SingleOrDefaultAsync(f => f.Id == family.Id));
    }

    [Fact]
    public async Task DeleteAsync_without_references_removes_the_family()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var service = new AssetFamilyService(db);
        var created = await service.CreateAsync(
            Family("courts", "Courts", isActive: true),
            CancellationToken.None);

        await service.DeleteAsync(created.Id, CancellationToken.None);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.GetByIdAsync(created.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListCatalogAsync_stays_active_only_after_an_inactive_create()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var service = new AssetFamilyService(db);
        var inactive = await service.CreateAsync(
            Family("legacy", "Legacy", isActive: false),
            CancellationToken.None);

        var catalog = await service.ListCatalogAsync(CancellationToken.None);
        Assert.DoesNotContain(catalog, family => family.Id == inactive.Id);

        var byId = await service.GetByIdAsync(inactive.Id, CancellationToken.None);
        Assert.False(byId.IsActive);

        var includingInactive = await service.ListCatalogIncludingInactiveAsync(CancellationToken.None);
        Assert.Contains(includingInactive, family => family.Id == inactive.Id && !family.IsActive);
    }

    private static CreateAssetFamilyRequest Family(string key, string label, bool isActive) =>
        new(key, label, [], 5, isActive);

    private static UpdateAssetFamilyRequest Update(
        AssetFamilyDetailResponse current,
        string label,
        bool isActive,
        string? key) =>
        new(label, current.Fields, current.SortOrder, isActive, key);

    private static AssetFamily CustomFamily(string key) =>
        new()
        {
            Key = key,
            Label = key,
            FieldSchemaJson = """{"fields":[]}""",
            SortOrder = 5,
            IsActive = true,
        };
}
