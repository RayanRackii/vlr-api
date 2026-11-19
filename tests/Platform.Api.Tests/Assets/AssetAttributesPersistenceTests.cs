using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Assets;

public sealed class AssetAttributesPersistenceTests
{
    private const string TypedSchema =
        """
        {"fields":[
          {"key":"capacity","type":"number","required":false},
          {"key":"covered","type":"boolean","required":false},
          {"key":"surface","type":"text","required":false},
          {"key":"materials","type":"multiselect","required":false,"options":["PLA","PETG"]}
        ]}
        """;

    private static Dictionary<string, JsonElement> Attrs(params (string Key, object? Value)[] items) =>
        items.ToDictionary(i => i.Key, i => JsonSerializer.SerializeToElement(i.Value), StringComparer.Ordinal);

    private static async Task UseSchemaAsync(BulkCreateAssetsHarness harness)
    {
        var family = await harness.Db.AssetFamilies.SingleAsync(f => f.Id == harness.FamilyId);
        family.FieldSchemaJson = TypedSchema;
        await harness.Db.SaveChangesAsync();
    }

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
    public async Task Typed_values_round_trip_through_create_and_update()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await UseSchemaAsync(harness);
        var registry = harness.CreateRegistry();

        var created = await registry.CreateRentableAsync(
            Create(harness, "P1", Attrs(
                ("capacity", 12), ("covered", true), ("surface", "Saibro"), ("materials", new[] { "PLA" }))),
            CancellationToken.None);

        harness.Db.ChangeTracker.Clear();
        var stored = await harness.Db.Assets.AsNoTracking().SingleAsync(a => a.Id == created.Id);
        Assert.Equal(JsonValueKind.Number, stored.Attributes["capacity"].ValueKind);
        Assert.Equal(12m, stored.Attributes["capacity"].GetDecimal());
        Assert.Equal(JsonValueKind.True, stored.Attributes["covered"].ValueKind);
        Assert.Equal("Saibro", stored.Attributes["surface"].GetString());
        Assert.Equal(JsonValueKind.Array, stored.Attributes["materials"].ValueKind);
        Assert.Equal("PLA", Assert.Single(stored.Attributes["materials"].EnumerateArray()).GetString());

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id,
            Update(harness, "P1", Attrs(("capacity", "20"), ("materials", "PLA, petg"))),
            CancellationToken.None);

        Assert.Equal(20m, updated.Attributes["capacity"].GetDecimal());
        Assert.Equal(
            new string?[] { "PLA", "PETG" },
            updated.Attributes["materials"].EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.DoesNotContain("covered", updated.Attributes.Keys);
    }

    [Fact]
    public async Task Legacy_string_values_are_read_unchanged_and_upgraded_on_the_next_write()
    {
        await using var harness = await BulkCreateAssetsHarness.CreateAsync();
        await UseSchemaAsync(harness);
        var registry = harness.CreateRegistry();
        var created = await registry.CreateRentableAsync(Create(harness, "P2", null), CancellationToken.None);

        var asset = await harness.Db.Assets.SingleAsync(a => a.Id == created.Id);
        asset.Attributes = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["capacity"] = JsonSerializer.SerializeToElement("12"),
            ["covered"] = JsonSerializer.SerializeToElement("true"),
        };
        await harness.Db.SaveChangesAsync();
        harness.Db.ChangeTracker.Clear();

        var rentals = new RentalAssetService(harness.Db, harness.TenantProvider);
        var read = await rentals.GetByAssetIdAsync(created.Id, CancellationToken.None);
        Assert.Equal(JsonValueKind.String, read!.Attributes!["capacity"].ValueKind);
        Assert.Equal("12", read.Attributes["capacity"].GetString());

        var updated = await registry.UpdateRentableAsync(
            created.RentalConfiguration!.Id,
            Update(harness, "P2", Attrs(("capacity", "12"), ("covered", "true"))),
            CancellationToken.None);

        Assert.Equal(JsonValueKind.Number, updated.Attributes["capacity"].ValueKind);
        Assert.Equal(12m, updated.Attributes["capacity"].GetDecimal());
        Assert.Equal(JsonValueKind.True, updated.Attributes["covered"].ValueKind);
    }
}
