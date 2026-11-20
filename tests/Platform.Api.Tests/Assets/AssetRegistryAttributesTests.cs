using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Assets;

public sealed class AssetRegistryAttributesTests
{
    private const string RequiredVoltageSchema =
        """{"fields":[{"key":"voltage","type":"number","required":true,"label":"Voltage"},{"key":"brand","type":"text"}]}""";

    private const string OptionalVoltageSchema =
        """{"fields":[{"key":"voltage","type":"number","required":false,"label":"Voltage"},{"key":"brand","type":"text"}]}""";

    private static async Task RequireVoltageAsync(BulkCreateAssetsHarness harness, string schema = RequiredVoltageSchema)
    {
        var family = await harness.Db.AssetFamilies.SingleAsync(f => f.Id == harness.FamilyId);
        family.FieldSchemaJson = schema;
        await harness.Db.SaveChangesAsync();
    }

    private static Dictionary<string, JsonElement> Attrs(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => JsonSerializer.SerializeToElement(i.Value), StringComparer.Ordinal);

    private static CreateRentableRequest Create(BulkCreateAssetsHarness harness, string tag, Dictionary<string, JsonElement>? attributes) =>
        new()
        {
            Name = $"Recurso {tag}",
            Tag = tag,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            RentalType = RentalAssetType.Location,
            TotalQuantity = 1,
            Attributes = attributes,
        };

    private static UpdateRentableRequest Update(BulkCreateAssetsHarness harness, string tag, Dictionary<string, JsonElement>? attributes) =>
        new()
        {
            Name = "Renomeado",
            Tag = tag,
            UnitId = harness.UnitId,
            CategoryId = harness.CategoryId,
            FamilyId = harness.FamilyId,
            RentalType = RentalAssetType.Location,
            TotalQuantity = 1,
            Attributes = attributes,
        };

    [Fact]
    public async Task CreateRentable_with_attributes_stores_them()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();

        var created = await registry.CreateRentableAsync(
            Create(harness, "E1", Attrs(("voltage", "220"), ("brand", " Acme "), ("unknown", "x"))),
            CancellationToken.None);

        Assert.Equal(220m, created.Attributes["voltage"].GetDecimal());
        Assert.Equal("Acme", created.Attributes["brand"].GetString());
        Assert.DoesNotContain("unknown", created.Attributes.Keys);
    }

    [Fact]
    public async Task CreateRentable_without_attributes_fails_naming_the_required_field()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.CreateRentableAsync(Create(harness, "E2", null), CancellationToken.None));

        Assert.Contains("voltage", ex.Message);
    }

    [Fact]
    public async Task UpdateRentable_legacy_asset_missing_new_required_field_keeps_values()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness, OptionalVoltageSchema);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(Create(harness, "E3", Attrs(("brand", "Acme"))), CancellationToken.None);
        await RequireVoltageAsync(harness);

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id, Update(harness, "E3", null), CancellationToken.None);

        Assert.Equal("Renomeado", updated.Name);
        Assert.Equal("Acme", updated.Attributes["brand"].GetString());
        Assert.DoesNotContain("voltage", updated.Attributes.Keys);
    }

    [Fact]
    public async Task UpdateRentable_with_attributes_replaces_and_validates()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            Create(harness, "E4", Attrs(("voltage", "110"), ("brand", "Acme"))), CancellationToken.None);
        var rentalId = created.RentalConfiguration!.Id;

        var updated = await registry.UpdateRentableAsync(
            rentalId, Update(harness, "E4", Attrs(("voltage", "220"))), CancellationToken.None);
        Assert.Equal(220m, updated.Attributes["voltage"].GetDecimal());
        Assert.DoesNotContain("brand", updated.Attributes.Keys);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateRentableAsync(rentalId, Update(harness, "E4", Attrs(("voltage", "abc"))), CancellationToken.None));
        // Dropping a required value that the asset already had is still rejected.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateRentableAsync(rentalId, Update(harness, "E4", Attrs(("brand", "x"))), CancellationToken.None));
    }

    [Fact]
    public async Task CreateRentable_family_not_enabled_for_tenant_is_rejected()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        var other = new AssetFamily { Key = $"other-{Guid.NewGuid():N}"[..32], Label = "Other", FieldSchemaJson = """{"fields":[]}""" };
        harness.Db.AssetFamilies.Add(other);
        await harness.Db.SaveChangesAsync();
        var registry = harness.CreateRegistry();

        var request = Create(harness, "E5", null) with { FamilyId = other.Id };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.CreateRentableAsync(request, CancellationToken.None));
        Assert.Contains("not enabled", ex.Message);
    }

    [Fact]
    public async Task UpdateRentable_without_attributes_keeps_a_stored_value_that_no_longer_parses()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness, """{"fields":[{"key":"voltage","type":"text","required":true}]}""");
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(Create(harness, "E6", Attrs(("voltage", "bivolt"))), CancellationToken.None);
        await RequireVoltageAsync(harness); // voltage is now a required number

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id, Update(harness, "E6", null), CancellationToken.None);

        Assert.Equal("bivolt", updated.Attributes["voltage"].GetString());
    }

    [Fact]
    public async Task Rental_asset_read_and_list_return_the_stored_attributes()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            Create(harness, "E7", Attrs(("voltage", "220"))), CancellationToken.None);
        var rentals = new RentalAssetService(harness.Db, harness.TenantProvider);

        var single = await rentals.GetByAssetIdAsync(created.Id, CancellationToken.None);
        var list = await rentals.ListRentableAsync(CancellationToken.None);

        Assert.Equal(220m, single!.Attributes!["voltage"].GetDecimal());
        Assert.Equal(220m, Assert.Single(list).Attributes!["voltage"].GetDecimal());
    }

    [Fact]
    public async Task Rental_assets_controller_create_returns_400_naming_the_missing_required_attribute()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var controller = new RentalAssetsController(
            new RentalAssetService(harness.Db, harness.TenantProvider), null!, harness.CreateRegistry());

        var result = await controller.Create(Create(harness, "E8", null), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("voltage", bad.Value!.ToString());
    }

    [Fact]
    public async Task Rental_assets_controller_update_returns_400_when_a_required_attribute_is_blanked()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            Create(harness, "E9", Attrs(("voltage", "220"))), CancellationToken.None);
        var controller = new RentalAssetsController(
            new RentalAssetService(harness.Db, harness.TenantProvider), null!, registry);

        var result = await controller.Update(
            created.RentalConfiguration!.Id, Update(harness, "E9", Attrs(("brand", "x"))), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("voltage", bad.Value!.ToString());
    }
}
