using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
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

    private static CreateRentableRequest Create(BulkCreateAssetsHarness harness, string tag, Dictionary<string, string?>? attributes) =>
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

    private static UpdateRentableRequest Update(BulkCreateAssetsHarness harness, string tag, Dictionary<string, string?>? attributes) =>
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
            Create(harness, "E1", new() { ["voltage"] = "220", ["brand"] = " Acme ", ["unknown"] = "x" }),
            CancellationToken.None);

        Assert.Equal("220", created.Attributes["voltage"]);
        Assert.Equal("Acme", created.Attributes["brand"]);
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
        var created = await registry.CreateRentableAsync(Create(harness, "E3", new() { ["brand"] = "Acme" }), CancellationToken.None);
        await RequireVoltageAsync(harness);

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id, Update(harness, "E3", null), CancellationToken.None);

        Assert.Equal("Renomeado", updated.Name);
        Assert.Equal("Acme", updated.Attributes["brand"]);
        Assert.DoesNotContain("voltage", updated.Attributes.Keys);
    }

    [Fact]
    public async Task UpdateRentable_with_attributes_replaces_and_validates()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await RequireVoltageAsync(harness);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(
            Create(harness, "E4", new() { ["voltage"] = "110", ["brand"] = "Acme" }), CancellationToken.None);
        var rentalId = created.RentalConfiguration!.Id;

        var updated = await registry.UpdateRentableAsync(
            rentalId, Update(harness, "E4", new() { ["voltage"] = "220" }), CancellationToken.None);
        Assert.Equal("220", updated.Attributes["voltage"]);
        Assert.DoesNotContain("brand", updated.Attributes.Keys);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateRentableAsync(rentalId, Update(harness, "E4", new() { ["voltage"] = "abc" }), CancellationToken.None));
        // Dropping a required value that the asset already had is still rejected.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.UpdateRentableAsync(rentalId, Update(harness, "E4", new() { ["brand"] = "x" }), CancellationToken.None));
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
}
