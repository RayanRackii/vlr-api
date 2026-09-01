using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

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
}
