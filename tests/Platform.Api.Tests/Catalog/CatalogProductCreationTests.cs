using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductCreationTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("Active", true)]
    [InlineData("Inactive", false)]
    [InlineData(" inactive ", false)]
    public async Task Initial_status_controls_publication_without_a_second_update(string? status, bool active)
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m, Status = status }, CancellationToken.None);

        Assert.Equal(active, product.IsActive);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(active, (await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.IsActive);
        Assert.Equal(active, (await h.Portal.ListProductsAsync(null, null, CancellationToken.None)).Any());
        Assert.Equal(active, await h.Portal.GetProductAsync(null, product.Id, CancellationToken.None) is not null);
    }

    [Theory]
    [InlineData("Published")]
    [InlineData("0")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task Invalid_initial_status_rejects_creation_without_persisting_a_product(string status)
    {
        await using var h = await CatalogHarness.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m, Status = status }, CancellationToken.None));
        Assert.Empty(await h.Db.CatalogProducts.AsNoTracking().ToListAsync());
    }
}
