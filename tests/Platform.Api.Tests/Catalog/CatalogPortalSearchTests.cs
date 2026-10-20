using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogPortalSearchTests
{
    [Theory]
    [InlineData("cafe", "Café especial", null)]
    [InlineData("CAFÉ", "cafe especial", null)]
    [InlineData("  ACAO  ", "Promoção de ação", null)]
    [InlineData("ac\u0327a\u0303o", "AÇÃO", null)]
    [InlineData("acao", "ac\u0327a\u0303o", null)]
    [InlineData("REF-CAFE", "Outro produto", "ref-café-01")]
    [InlineData("réf-café", "Outro produto", "REF-CAFE-01")]
    [InlineData("%", "Oferta 10%", null)]
    [InlineData("_", "Outro produto", "REF_01")]
    [InlineData("\\", "Outro produto", "REF\\01")]
    public async Task Search_matches_name_or_code_ignoring_case_and_accents(
        string search, string name, string? code)
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var match = new CatalogProduct { TenantId = harness.Tenant.Id, Name = name, Code = code };
        harness.Db.CatalogProducts.AddRange(match,
            new CatalogProduct { TenantId = harness.Tenant.Id, Name = "Sem correspondência", Code = null });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Portal.ListProductsAsync(search, CancellationToken.None);

        Assert.Equal(match.Id, Assert.Single(result).Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cafe")]
    public async Task Search_keeps_tenant_and_active_filters_and_name_order(string? search)
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var otherTenant = new Tenant("Other", "55555555000191");
        harness.Db.Tenants.Add(otherTenant);
        var first = new CatalogProduct { TenantId = harness.Tenant.Id, Name = "Café A" };
        var last = new CatalogProduct { TenantId = harness.Tenant.Id, Name = "Café Z" };
        harness.Db.CatalogProducts.AddRange(last, first,
            new CatalogProduct { TenantId = harness.Tenant.Id, Name = "Café inativo", IsActive = false },
            new CatalogProduct { TenantId = otherTenant.Id, Name = "Café outro tenant" });
        await harness.Db.SaveChangesAsync();

        var result = await harness.Portal.ListProductsAsync(search, CancellationToken.None);

        Assert.Equal(new[] { first.Id, last.Id }, result.Select(p => p.Id));
    }

    [Fact]
    public async Task Search_without_matches_returns_empty()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        harness.Db.CatalogProducts.Add(new CatalogProduct { TenantId = harness.Tenant.Id, Name = "Café" });
        await harness.Db.SaveChangesAsync();

        Assert.Empty(await harness.Portal.ListProductsAsync("inexistente", CancellationToken.None));
    }
}
