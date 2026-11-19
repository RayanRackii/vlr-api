using System.Text.Json;
using Platform.Api.Modules.Assets.Services;

namespace Platform.Api.Tests.Assets;

/// <summary>Pure tests of the evolution planner: no database, no tenant provider.</summary>
public sealed class AssetFamilySchemaEvolutionTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    [Fact]
    public void Matrix_reports_one_change_per_key()
    {
        var stored = Schema(
            Def("same", "text"),
            Def("tuned", "number"),
            Def("retyped", "text"),
            Def("gone_with_values", "text"),
            Def("gone_empty", "text"),
            Def("shelved", "text") with { Archived = true },
            Def("restored", "text") with { Archived = true });
        var requested = new[]
        {
            Def("same", "text"),
            Def("tuned", "number") with { Unit = "mm" },
            Def("retyped", "longtext"),
            Def("fresh", "text"),
            Def("restored", "text"),
        };
        var assets = new[]
        {
            Asset(TenantA, ("gone_with_values", "x"), ("retyped", "y")),
            Asset(TenantB, ("shelved", "z")),
        };

        var plan = AssetFamilySchemaEvolution.Plan(stored, requested, assets);

        string Change(string key) => plan.Impacts.Single(i => i.Key == key).Change;
        Assert.Equal(AssetFamilySchemaEvolution.Unchanged, Change("same"));
        Assert.Equal(AssetFamilySchemaEvolution.Updated, Change("tuned"));
        Assert.Equal(AssetFamilySchemaEvolution.TypeChanged, Change("retyped"));
        Assert.Equal(AssetFamilySchemaEvolution.Added, Change("fresh"));
        Assert.Equal(AssetFamilySchemaEvolution.Unarchived, Change("restored"));
        Assert.Equal(AssetFamilySchemaEvolution.Archived, Change("gone_with_values"));
        Assert.Equal(AssetFamilySchemaEvolution.Removed, Change("gone_empty"));
        Assert.Equal(AssetFamilySchemaEvolution.Unchanged, Change("shelved"));
        Assert.True(plan.CanSave);

        // Auto-archived and already archived definitions are kept, after the requested fields.
        var keys = plan.Fields.Select(f => f.Key).ToArray();
        Assert.Equal(
            new[] { "same", "tuned", "retyped", "fresh", "restored", "gone_with_values", "shelved" },
            keys);
        Assert.True(plan.Fields.Single(f => f.Key == "gone_with_values").Archived);
        Assert.DoesNotContain(plan.Fields, f => f.Key == "gone_empty");
        Assert.Equal(2, plan.AssetCount);
        Assert.Equal(2, plan.TenantCount);
    }

    [Fact]
    public void Auto_archive_drops_required()
    {
        var stored = Schema(Def("owner", "text") with { Required = true });

        var plan = AssetFamilySchemaEvolution.Plan(stored, [], [Asset(TenantA, ("owner", "Ana"))]);

        var kept = Assert.Single(plan.Fields);
        Assert.True(kept.Archived);
        Assert.False(kept.Required);
    }

    [Fact]
    public void Absent_values_do_not_count_as_holding_a_value()
    {
        var stored = Schema(Def("notes", "text"));
        var assets = new[]
        {
            Asset(TenantA, ("notes", "")),
            Asset(TenantA, ("notes", "   ")),
            Asset(TenantB),
        };

        var plan = AssetFamilySchemaEvolution.Plan(stored, [], assets);

        var impact = Assert.Single(plan.Impacts);
        Assert.Equal(AssetFamilySchemaEvolution.Removed, impact.Change);
        Assert.Equal(0, impact.AssetCount);
        Assert.Empty(plan.Fields);
    }

    [Fact]
    public void Type_change_collects_up_to_five_distinct_samples_truncated_to_forty_chars()
    {
        var stored = Schema(Def("weight", "text"));
        var assets = Enumerable.Range(0, 8)
            .Select(i => Asset(i % 2 == 0 ? TenantA : TenantB, ("weight", $"bad{i}")))
            .Append(Asset(TenantA, ("weight", new string('x', 60))))
            .Append(Asset(TenantA, ("weight", "bad0")))
            .Append(Asset(TenantA, ("weight", "12")))
            .ToArray();

        var plan = AssetFamilySchemaEvolution.Plan(stored, [Def("weight", "number")], assets);

        var impact = Assert.Single(plan.Impacts);
        Assert.True(impact.Blocking);
        Assert.False(plan.CanSave);
        Assert.Equal(10, impact.IncompatibleCount);
        Assert.Equal(11, impact.AssetCount);
        Assert.Equal(5, impact.IncompatibleSamples.Count);
        Assert.Equal(5, impact.IncompatibleSamples.Distinct().Count());
        Assert.All(impact.IncompatibleSamples, s => Assert.True(s.Length <= 40));
        Assert.Single(plan.BlockingMessages);
    }

    [Fact]
    public void Same_type_is_never_blocking_and_counts_violations_of_the_new_limits()
    {
        var stored = Schema(Def("capacity", "number"));
        var assets = new[]
        {
            Asset(TenantA, ("capacity", 10)),
            Asset(TenantA, ("capacity", "500")),
            Asset(TenantB, ("capacity", "not a number")),
        };

        var plan = AssetFamilySchemaEvolution.Plan(
            stored, [Def("capacity", "number") with { Max = 100 }], assets);

        var impact = Assert.Single(plan.Impacts);
        Assert.False(impact.Blocking);
        Assert.Equal(0, impact.IncompatibleCount);
        Assert.Equal(2, impact.ConstraintViolationCount);
        Assert.True(plan.CanSave);
    }

    [Fact]
    public void Safe_upgrades_pass_and_option_mismatch_blocks()
    {
        var options = new[] { "PLA", "PETG", "ABS", "PLA especiais" };

        bool IsBlocked(string fromType, string toType, string value, Func<AssetFieldDefinitionDto, AssetFieldDefinitionDto>? tune = null)
        {
            var target = Def("f", toType);
            if (tune is not null)
            {
                target = tune(target);
            }

            var plan = AssetFamilySchemaEvolution.Plan(
                Schema(Def("f", fromType)), [target], [Asset(TenantA, ("f", value))]);
            return plan.Impacts.Single().Blocking;
        }

        Assert.False(IsBlocked("text", "longtext", "linha 1\nlinha 2"));
        Assert.False(IsBlocked("text", "url", "https://example.com/a"));
        Assert.True(IsBlocked("text", "url", "example.com"));
        Assert.False(IsBlocked("text", "multiselect", "PLA, PETG", d => d with { Options = options }));
        Assert.True(IsBlocked("text", "multiselect", "PLA especial", d => d with { Options = options }));
        Assert.False(IsBlocked("select", "multiselect", "PLA", d => d with { Options = options }));
        Assert.False(IsBlocked("number", "text", "12"));
        Assert.False(IsBlocked("number", "number", "256", d => d with { Unit = "mm", Min = 0 }));
        Assert.False(IsBlocked("text", "number", "1.5"));
        Assert.True(IsBlocked("text", "number", "0,02"));
    }

    [Fact]
    public void Added_key_with_orphan_values_must_validate()
    {
        var stored = Schema(Def("surface", "text"));
        var requested = new[] { Def("surface", "text"), Def("legacy", "number") };

        var bad = AssetFamilySchemaEvolution.Plan(stored, requested, [Asset(TenantA, ("legacy", "abc"))]);
        var good = AssetFamilySchemaEvolution.Plan(stored, requested, [Asset(TenantA, ("legacy", "12"))]);
        var archivedAdd = AssetFamilySchemaEvolution.Plan(
            stored,
            [Def("surface", "text"), Def("legacy", "number") with { Archived = true }],
            [Asset(TenantA, ("legacy", "abc"))]);

        Assert.False(bad.CanSave);
        Assert.True(good.CanSave);
        Assert.True(archivedAdd.CanSave);
        Assert.Equal(AssetFamilySchemaEvolution.Added, bad.Impacts.Single(i => i.Key == "legacy").Change);
    }

    [Fact]
    public void Unarchive_with_a_type_change_checks_values()
    {
        var stored = Schema(Def("old", "text") with { Archived = true });

        var blocked = AssetFamilySchemaEvolution.Plan(
            stored, [Def("old", "number")], [Asset(TenantA, ("old", "abc"))]);
        var sameType = AssetFamilySchemaEvolution.Plan(
            stored, [Def("old", "text")], [Asset(TenantA, ("old", "abc"))]);

        Assert.False(blocked.CanSave);
        Assert.True(sameType.CanSave);
        Assert.Equal(AssetFamilySchemaEvolution.Unarchived, sameType.Impacts.Single().Change);
    }

    [Fact]
    public void SerializeFields_is_stable_for_equal_lists()
    {
        var a = AssetFamilySchemaEvolution.SerializeFields([Def("x", "select") with { Options = ["A", "B"] }]);
        var b = AssetFamilySchemaEvolution.SerializeFields([Def("x", "select") with { Options = ["A", "B"] }]);

        Assert.Equal(a, b);
    }

    private static AssetFieldDefinitionDto Def(string key, string type) =>
        new(key, type, false, key);

    private static AssetFieldSchemaDto Schema(params AssetFieldDefinitionDto[] fields) =>
        new(fields, 1);

    private static (Guid TenantId, IReadOnlyDictionary<string, JsonElement> Attributes) Asset(
        Guid tenantId,
        params (string Key, object Value)[] attributes) =>
        (tenantId,
            attributes.ToDictionary(
                a => a.Key,
                a => JsonSerializer.SerializeToElement(a.Value),
                StringComparer.Ordinal));
}
