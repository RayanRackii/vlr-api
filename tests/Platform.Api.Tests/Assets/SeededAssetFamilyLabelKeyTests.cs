using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Assets;

public sealed class SeededAssetFamilyLabelKeyTests
{
    [Fact]
    public async Task Seeded_family_and_fields_get_label_keys_and_keep_their_label()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        db.AssetFamilies.Add(new AssetFamily
        {
            Key = "goods",
            Label = "Bens e itens",
            FieldSchemaJson = """{"fields":[{"key":"quantity_hint","type":"number","required":false,"label":"Quantidade típica"}]}""",
            SortOrder = 30,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var catalog = await new AssetFamilyService(db).ListCatalogAsync(CancellationToken.None);

        var family = Assert.Single(catalog);
        Assert.Equal("Bens e itens", family.Label);
        Assert.Equal("assetFamilies.goods.label", family.LabelKey);
        var field = Assert.Single(family.Fields);
        Assert.Equal("Quantidade típica", field.Label);
        Assert.Equal("assetFamilies.goods.fields.quantity_hint.label", field.LabelKey);
    }

    [Fact]
    public async Task Edited_labels_and_custom_families_get_no_label_key()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        db.AssetFamilies.AddRange(
            new AssetFamily
            {
                Key = "goods",
                Label = "Mercadorias",
                FieldSchemaJson = """{"fields":[{"key":"quantity_hint","type":"number","required":false,"label":"Qtd"}]}""",
                SortOrder = 30,
                IsActive = true,
            },
            new AssetFamily
            {
                Key = "courts",
                Label = "Quadras",
                FieldSchemaJson = """{"fields":[{"key":"surface","type":"text","required":false,"label":"Piso"}]}""",
                SortOrder = 40,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        var catalog = await new AssetFamilyService(db).ListCatalogAsync(CancellationToken.None);

        Assert.All(catalog, family =>
        {
            Assert.Null(family.LabelKey);
            Assert.All(family.Fields, field => Assert.Null(field.LabelKey));
        });
    }

    [Fact]
    public void Label_key_helpers_match_only_the_seeded_text()
    {
        Assert.Equal("assetFamilies.spaces.label", SeededAssetFamilyLabels.FamilyKey("spaces", "Espaços"));
        Assert.Null(SeededAssetFamilyLabels.FamilyKey("spaces", "Spaces"));
        Assert.Null(SeededAssetFamilyLabels.FamilyKey("unknown", "Espaços"));
        Assert.Equal(
            "assetFamilies.electrical.fields.voltage.label",
            SeededAssetFamilyLabels.FieldKey("electrical", "voltage", "Voltagem"));
        Assert.Null(SeededAssetFamilyLabels.FieldKey("electrical", "voltage", "Voltagem (optional)"));
    }

    [Fact]
    public void Label_cleanup_migration_is_data_only_guarded_and_pinned()
    {
        var migration = new CorrectSeededGoodsFamilyFieldLabel();
        var operation = Assert.Single(migration.UpOperations);
        Assert.Empty(migration.DownOperations);
        var sql = Assert.IsType<SqlOperation>(operation).Sql;
        Assert.Contains("WHERE key = 'goods'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE '%Quantidade típica (optional)%'", sql, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "DELETE ", "DROP ", "TRUNCATE", "ALTER ", "CREATE ", "INSERT " })
        {
            Assert.DoesNotContain(forbidden, sql, StringComparison.OrdinalIgnoreCase);
        }

        var id = typeof(CorrectSeededGoodsFamilyFieldLabel).GetCustomAttribute<MigrationAttribute>()!.Id;
        Assert.Equal("20261204100000_CorrectSeededGoodsFamilyFieldLabel", id);
        Assert.True(
            string.CompareOrdinal(id, "20261202100000_AddSubscriptionProviderBillingState") > 0);
    }
}
