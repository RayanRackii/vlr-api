using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Serialization;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductPriceUnitTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Create_without_a_unit_returns_null_everywhere()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 10m }, Ct);

        Assert.Null(product.PriceUnit);
        h.Db.ChangeTracker.Clear();
        Assert.Null((await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);
        Assert.Null((await h.Portal.ListProductsAsync(null, null, Ct)).Single().PriceUnit);
    }

    [Theory]
    [InlineData("Unit", "Unit")]
    [InlineData("hour", "Hour")]
    [InlineData(" DAY ", "Day")]
    [InlineData("Person", "Person")]
    [InlineData("booking", "Booking")]
    [InlineData("Package", "Package")]
    public async Task Create_accepts_each_unit_case_insensitively_and_reads_it_back_in_staff_and_portal_dtos(
        string input,
        string expected)
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 45m, PriceUnit = input }, Ct);

        Assert.Equal(expected, product.PriceUnit);
        h.Db.ChangeTracker.Clear();
        Assert.Equal(expected, (await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);
        Assert.Equal(expected, (await h.Products.ListAsync(new CatalogProductListQuery(null, null, null), Ct)).Single().PriceUnit);
        Assert.Equal(expected, (await h.Portal.ListProductsAsync(null, null, Ct)).Single().PriceUnit);
        Assert.Equal(expected, (await h.Portal.GetProductAsync(null, product.Id, Ct))!.PriceUnit);
    }

    [Fact]
    public async Task Blank_unit_means_none()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", PriceUnit = "   " }, Ct);

        Assert.Null(product.PriceUnit);
    }

    [Theory]
    [InlineData("Weekly")]
    [InlineData("por hora")]
    [InlineData("5")]
    public async Task Create_and_update_reject_values_outside_the_list(string value)
    {
        await using var h = await CatalogHarness.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", PriceUnit = value }, Ct));
        Assert.Empty(await h.Products.ListAsync(new CatalogProductListQuery(null, null, null), Ct));

        var product = await h.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", PriceUnit = "Hour" }, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => h.Products.UpdateAsync(
            product.Id, new UpdateCatalogProductRequest { Name = "Item", PriceUnit = value }, Ct));
        h.Db.ChangeTracker.Clear();
        Assert.Equal("Hour", (await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);
    }

    [Fact]
    public async Task Update_sets_changes_and_clears_the_unit()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 5m }, Ct);

        await h.Products.UpdateAsync(product.Id, new UpdateCatalogProductRequest { Name = "Item", Price = 5m, PriceUnit = "Day" }, Ct);
        h.Db.ChangeTracker.Clear();
        Assert.Equal("Day", (await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);

        await h.Products.UpdateAsync(product.Id, new UpdateCatalogProductRequest { Name = "Item", Price = 5m, PriceUnit = "Package" }, Ct);
        h.Db.ChangeTracker.Clear();
        Assert.Equal("Package", (await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);

        await h.Products.UpdateAsync(product.Id, new UpdateCatalogProductRequest { Name = "Item", Price = 5m, PriceUnit = JsonOptionalString.Specified(null) }, Ct);
        h.Db.ChangeTracker.Clear();
        Assert.Null((await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);
    }

    [Fact]
    public async Task Update_without_a_unit_keeps_it_and_blank_clears_it()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 5m, PriceUnit = "Hour" }, Ct);

        await h.Products.UpdateAsync(product.Id, new UpdateCatalogProductRequest { Name = "Renamed", Price = 5m }, Ct);
        h.Db.ChangeTracker.Clear();
        Assert.Equal("Hour", (await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);

        await h.Products.UpdateAsync(product.Id, new UpdateCatalogProductRequest { Name = "Renamed", Price = 5m, PriceUnit = "  " }, Ct);
        h.Db.ChangeTracker.Clear();
        Assert.Null((await h.Products.GetByIdAsync(product.Id, Ct))!.PriceUnit);
    }

    [Fact]
    public void Update_request_distinguishes_absent_null_and_value_in_json()
    {
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var absent = System.Text.Json.JsonSerializer.Deserialize<UpdateCatalogProductRequest>("{\"name\":\"a\"}", web)!;
        var explicitNull = System.Text.Json.JsonSerializer.Deserialize<UpdateCatalogProductRequest>("{\"name\":\"a\",\"priceUnit\":null}", web)!;
        var value = System.Text.Json.JsonSerializer.Deserialize<UpdateCatalogProductRequest>("{\"name\":\"a\",\"priceUnit\":\"Day\"}", web)!;

        Assert.False(absent.PriceUnit.IsSpecified);
        Assert.True(explicitNull.PriceUnit.IsSpecified);
        Assert.Null(explicitNull.PriceUnit.Value);
        Assert.True(value.PriceUnit.IsSpecified);
        Assert.Equal("Day", value.PriceUnit.Value);
    }

    [Fact]
    public void History_diff_tracks_the_price_unit_only_when_it_changes()
    {
        var before = new CatalogProductHistoryFields("Item", null, null, 10m, "BRL", true, null);

        Assert.Empty(CatalogProductHistoryDiff.ForUpdate(before, before));

        var set = Assert.Single(CatalogProductHistoryDiff.ForUpdate(before, before with { PriceUnit = "Hour" }));
        Assert.Equal("priceUnit", set.Field);
        Assert.Null(set.Old);
        Assert.Equal("Hour", set.New);

        var changed = Assert.Single(CatalogProductHistoryDiff.ForUpdate(
            before with { PriceUnit = "Hour" }, before with { PriceUnit = "Day" }));
        Assert.Equal("Hour", changed.Old);
        Assert.Equal("Day", changed.New);
    }
}
