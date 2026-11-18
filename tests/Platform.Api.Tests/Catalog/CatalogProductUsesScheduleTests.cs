using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductUsesScheduleTests
{
    [Fact]
    public async Task Create_defaults_uses_schedule_to_true_and_read_returns_it()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);

        Assert.True(product.UsesSchedule);
        h.Db.ChangeTracker.Clear();
        Assert.True((await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.UsesSchedule);
        Assert.True((await h.Products.ListAsync(new CatalogProductListQuery(null, null, null), CancellationToken.None))[0].UsesSchedule);
    }

    [Fact]
    public async Task Create_can_opt_out_of_the_schedule()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", UsesSchedule = false }, CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        Assert.False((await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.UsesSchedule);
    }

    [Fact]
    public async Task Update_persists_uses_schedule_and_absent_value_keeps_it()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item" }, CancellationToken.None);

        await h.Products.UpdateAsync(product.Id,
            new UpdateCatalogProductRequest { Name = "Item", UsesSchedule = false }, CancellationToken.None);
        h.Db.ChangeTracker.Clear();
        Assert.False((await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.UsesSchedule);

        await h.Products.UpdateAsync(product.Id,
            new UpdateCatalogProductRequest { Name = "Renamed" }, CancellationToken.None);
        h.Db.ChangeTracker.Clear();
        Assert.False((await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.UsesSchedule);

        await h.Products.UpdateAsync(product.Id,
            new UpdateCatalogProductRequest { Name = "Renamed", UsesSchedule = true }, CancellationToken.None);
        h.Db.ChangeTracker.Clear();
        Assert.True((await h.Products.GetByIdAsync(product.Id, CancellationToken.None))!.UsesSchedule);
    }

    [Fact]
    public void Migration_adds_a_non_null_boolean_column_defaulting_to_true_and_nothing_else()
    {
        var operations = new AddCatalogProductUsesSchedule().UpOperations;

        var add = Assert.IsType<AddColumnOperation>(Assert.Single(operations));
        Assert.Equal("catalog_products", add.Table);
        Assert.Equal("catalog", add.Schema);
        Assert.Equal("uses_schedule", add.Name);
        Assert.False(add.IsNullable);
        Assert.Equal(true, add.DefaultValue);
    }
}
