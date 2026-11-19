using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Assets;

/// <summary>Schema evolution through <see cref="AssetFamilyService"/> (UpdateAsync and PreviewSchemaAsync).</summary>
public sealed class AssetFamilySchemaEvolutionServiceTests
{
    private static readonly string[] PrinterMaterials = ["PLA", "PETG", "ABS", "TPU", "ASA", "PLA especiais"];

    [Fact]
    public async Task Omitted_field_with_values_is_archived_and_values_are_untouched()
    {
        var (db, service, family, tenants) = await ArrangeAsync(
            [F("capacity", "number"), F("surface", "text")]);
        await using var _ = db;
        var asset = await SeedAssetAsync(db, tenants[0], family, "A1", ("capacity", "12"));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", [F("surface", "text")], 1, true),
            CancellationToken.None);

        var archived = Assert.Single(updated.Fields, f => f.Key == "capacity");
        Assert.True(archived.Archived);
        Assert.False(archived.Required);
        Assert.Equal(2, updated.SchemaVersion);

        var stored = await db.Assets.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        Assert.Equal("12", stored.Attributes["capacity"].GetString());

        var catalog = await service.ListCatalogAsync(CancellationToken.None);
        Assert.DoesNotContain(
            catalog.Single(f => f.Id == family.Id).Fields,
            f => f.Key == "capacity");
        var byId = await service.GetByIdAsync(family.Id, CancellationToken.None);
        Assert.Contains(byId.Fields, f => f.Key == "capacity" && f.Archived);
    }

    [Fact]
    public async Task Omitted_field_without_values_is_removed()
    {
        var (db, service, family, tenants) = await ArrangeAsync(
            [F("capacity", "number"), F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("surface", "Saibro"));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", [F("surface", "text")], 1, true),
            CancellationToken.None);

        Assert.DoesNotContain(updated.Fields, f => f.Key == "capacity");
        Assert.Single(updated.Fields);
    }

    [Fact]
    public async Task Text_to_longtext_and_number_with_unit_are_safe_upgrades()
    {
        var (db, service, family, tenants) = await ArrangeAsync(
            [F("notes", "text"), F("build_x_mm", "number")]);
        await using var _ = db;
        var asset = await SeedAssetAsync(db, tenants[0], family, "A1", ("notes", "texto"), ("build_x_mm", "256"));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest(
                "Fam",
                [
                    F("notes", "longtext"),
                    F("build_x_mm", "number") with { Unit = "mm", Min = 0, Decimals = 1, Group = "Volume" },
                ],
                1,
                true),
            CancellationToken.None);

        Assert.Equal("longtext", updated.Fields[0].Type);
        Assert.Equal("mm", updated.Fields[1].Unit);
        Assert.Equal(2, updated.SchemaVersion);

        // A family update never rewrites asset values.
        var stored = await db.Assets.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        Assert.Equal(JsonValueKind.String, stored.Attributes["build_x_mm"].ValueKind);
    }

    [Fact]
    public async Task Text_to_number_is_blocked_by_an_incompatible_value_and_passes_with_dot_numbers()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("weight", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("weight", "12"));
        await SeedAssetAsync(db, tenants[0], family, "A2", ("weight", "abc"));

        var request = new UpdateAssetFamilyRequest("Fam", [F("weight", "number")], 1, true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(family.Id, request, CancellationToken.None));
        Assert.Contains("weight", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from text to number", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1 asset holds an incompatible value", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'abc'", ex.Message, StringComparison.Ordinal);

        var preview = await service.PreviewSchemaAsync(
            family.Id,
            new AssetFamilySchemaPreviewRequest(request.Fields),
            CancellationToken.None);
        var impact = Assert.Single(preview.Fields);
        Assert.False(preview.CanSave);
        Assert.True(impact.Blocking);
        Assert.Equal(AssetFamilySchemaEvolution.TypeChanged, impact.Change);
        Assert.Equal(1, impact.IncompatibleCount);
        Assert.Equal(new[] { "abc" }, impact.IncompatibleSamples);

        var unchanged = await service.GetByIdAsync(family.Id, CancellationToken.None);
        Assert.Equal("text", unchanged.Fields.Single().Type);
        Assert.Equal(1, unchanged.SchemaVersion);
    }

    [Fact]
    public async Task Text_to_number_passes_when_every_value_parses()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("weight", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("weight", "12"));
        await SeedAssetAsync(db, tenants[0], family, "A2", ("weight", "1.5"));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", [F("weight", "number")], 1, true),
            CancellationToken.None);

        Assert.Equal("number", updated.Fields.Single().Type);
    }

    [Fact]
    public async Task Text_to_multiselect_splits_on_commas_and_names_the_unmatched_token()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("materials", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("materials", "PLA, PETG"));

        var multiselect = F("materials", "multiselect") with { Options = PrinterMaterials };
        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", [multiselect], 1, true),
            CancellationToken.None);
        Assert.Equal("multiselect", updated.Fields.Single().Type);

        // Separate family: the stored token does not match the option "PLA especiais".
        var (db2, service2, family2, tenants2) = await ArrangeAsync([F("materials", "text")]);
        await using var _2 = db2;
        await SeedAssetAsync(db2, tenants2[0], family2, "B1", ("materials", "PLA especial"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service2.UpdateAsync(
                family2.Id,
                new UpdateAssetFamilyRequest("Fam", [multiselect], 1, true),
                CancellationToken.None));
        Assert.Contains("'PLA especial'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("from text to multiselect", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Text_to_url_is_blocked_by_an_invalid_stored_value()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("source_url", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("source_url", "https://example.com/spec"));
        await SeedAssetAsync(db, tenants[0], family, "A2", ("source_url", "not a url"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest("Fam", [F("source_url", "url")], 1, true),
                CancellationToken.None));

        Assert.Contains("'not a url'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tightened_constraint_on_the_same_type_is_a_warning_not_a_block()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("capacity", "number")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("capacity", 50));
        await SeedAssetAsync(db, tenants[0], family, "A2", ("capacity", 500));

        var tightened = new[] { F("capacity", "number") with { Max = 100 } };
        var preview = await service.PreviewSchemaAsync(
            family.Id,
            new AssetFamilySchemaPreviewRequest(tightened),
            CancellationToken.None);

        var impact = Assert.Single(preview.Fields);
        Assert.True(preview.CanSave);
        Assert.False(impact.Blocking);
        Assert.Equal(AssetFamilySchemaEvolution.Updated, impact.Change);
        Assert.Equal(1, impact.ConstraintViolationCount);

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", tightened, 1, true),
            CancellationToken.None);
        Assert.Equal(100m, updated.Fields.Single().Max);
    }

    [Fact]
    public async Task New_required_field_is_saved_and_existing_assets_stay_editable()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("surface", "Saibro"));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest(
                "Fam",
                [F("surface", "text"), F("owner", "text") with { Required = true }],
                1,
                true),
            CancellationToken.None);
        Assert.True(updated.Fields.Single(f => f.Key == "owner").Required);

        var stored = await db.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == family.Id);
        var existing = new Dictionary<string, JsonElement> { ["surface"] = JsonSerializer.SerializeToElement("Saibro") };
        var projected = AssetFamilyAttributeValidator.ValidateAndProject(
            stored.FieldSchemaJson,
            new Dictionary<string, JsonElement> { ["surface"] = JsonSerializer.SerializeToElement("Grama") },
            existing,
            isUpdate: true);
        Assert.Equal("Grama", projected["surface"].GetString());

        Assert.Throws<ArgumentException>(() =>
            AssetFamilyAttributeValidator.ValidateAndProject(
                stored.FieldSchemaJson,
                new Dictionary<string, JsonElement> { ["surface"] = JsonSerializer.SerializeToElement("Grama") },
                existing: null,
                isUpdate: false));
    }

    [Fact]
    public async Task Orphan_values_under_a_re_added_key_must_validate()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("legacy_weight", "abc"));

        var request = new UpdateAssetFamilyRequest(
            "Fam", [F("surface", "text"), F("legacy_weight", "number")], 1, true);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(family.Id, request, CancellationToken.None));
        Assert.Contains("legacy_weight", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'abc'", ex.Message, StringComparison.Ordinal);

        var asText = new UpdateAssetFamilyRequest(
            "Fam", [F("surface", "text"), F("legacy_weight", "text")], 1, true);
        var updated = await service.UpdateAsync(family.Id, asText, CancellationToken.None);
        Assert.Equal(2, updated.Fields.Count);
    }

    [Fact]
    public async Task Preview_counts_assets_and_tenants_across_tenants_with_an_ambient_tenant()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("capacity", "number"), F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("capacity", 10));
        await SeedAssetAsync(db, tenants[0], family, "A2", ("capacity", 20));
        await SeedAssetAsync(db, tenants[1], family, "B1", ("capacity", 30), ("surface", "Saibro"));
        await SeedAssetAsync(db, tenants[1], family, "B2", ("surface", ""));

        // A platform admin who entered tenant 0 still sees counts from tenant 1.
        TenantProvider(db).TenantId = tenants[0].Id;

        var preview = await service.PreviewSchemaAsync(
            family.Id,
            new AssetFamilySchemaPreviewRequest([F("surface", "text")]),
            CancellationToken.None);

        Assert.Equal(4, preview.AssetCount);
        Assert.Equal(2, preview.TenantCount);
        var capacity = Assert.Single(preview.Fields, f => f.Key == "capacity");
        Assert.Equal(AssetFamilySchemaEvolution.Archived, capacity.Change);
        Assert.Equal(3, capacity.AssetCount);
        Assert.Equal(2, capacity.TenantCount);
        var surface = Assert.Single(preview.Fields, f => f.Key == "surface");
        Assert.Equal(AssetFamilySchemaEvolution.Unchanged, surface.Change);
        Assert.Equal(1, surface.AssetCount);
        Assert.Equal(1, surface.TenantCount);
    }

    [Fact]
    public async Task Version_is_bumped_on_change_and_kept_on_a_no_op_save()
    {
        var (db, service, family, _) = await ArrangeAsync([F("surface", "text")]);
        await using var _db = db;

        var current = await service.GetByIdAsync(family.Id, CancellationToken.None);
        Assert.Equal(1, current.SchemaVersion);

        var noOp = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", current.Fields, 1, true, ExpectedSchemaVersion: 1),
            CancellationToken.None);
        Assert.Equal(1, noOp.SchemaVersion);

        var changed = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest(
                "Fam", [F("surface", "text") with { Help = "Piso da quadra" }], 1, true, ExpectedSchemaVersion: 1),
            CancellationToken.None);
        Assert.Equal(2, changed.SchemaVersion);

        var stored = await db.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == family.Id);
        Assert.Contains("\"version\":2", stored.FieldSchemaJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stale_expected_schema_version_returns_conflict_and_saves_nothing()
    {
        var (db, service, family, _) = await ArrangeAsync([F("surface", "text")]);
        await using var _db = db;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest(
                    "Renamed", [F("surface", "text"), F("extra", "text")], 1, true, ExpectedSchemaVersion: 7),
                CancellationToken.None));

        Assert.Contains("current version 1", ex.Message, StringComparison.Ordinal);
        var stored = await service.GetByIdAsync(family.Id, CancellationToken.None);
        Assert.Equal("Fam", stored.Label);
        Assert.Single(stored.Fields);
    }

    [Fact]
    public async Task Preview_does_not_persist_and_agrees_with_update_on_blocking()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("weight", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("weight", "abc"));
        var before = (await db.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == family.Id)).FieldSchemaJson;

        var fields = new[] { F("weight", "number") };
        var preview = await service.PreviewSchemaAsync(
            family.Id, new AssetFamilySchemaPreviewRequest(fields), CancellationToken.None);

        var after = (await db.AssetFamilies.AsNoTracking().SingleAsync(f => f.Id == family.Id)).FieldSchemaJson;
        Assert.Equal(before, after);
        Assert.False(preview.CanSave);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(family.Id, new UpdateAssetFamilyRequest("Fam", fields, 1, true), CancellationToken.None));

        var ok = new[] { F("weight", "text") };
        var okPreview = await service.PreviewSchemaAsync(
            family.Id, new AssetFamilySchemaPreviewRequest(ok), CancellationToken.None);
        Assert.True(okPreview.CanSave);
        await service.UpdateAsync(family.Id, new UpdateAssetFamilyRequest("Fam", ok, 1, true), CancellationToken.None);
    }

    [Fact]
    public async Task Preview_of_an_unknown_family_is_not_found_and_an_invalid_definition_is_rejected()
    {
        var (db, service, family, _) = await ArrangeAsync([F("surface", "text")]);
        await using var _db = db;

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.PreviewSchemaAsync(
                Guid.NewGuid(), new AssetFamilySchemaPreviewRequest([]), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PreviewSchemaAsync(
                family.Id, new AssetFamilySchemaPreviewRequest([F("Bad Key", "text")]), CancellationToken.None));
    }

    [Fact]
    public async Task Non_conforming_stored_key_is_grandfathered_but_not_accepted_as_a_new_field()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var family = new AssetFamily
        {
            Key = "legacy",
            Label = "Legacy",
            FieldSchemaJson = """{"fields":[{"key":"Old-Key","type":"text","required":false,"label":"Old"}]}""",
            SortOrder = 1,
            IsActive = true,
        };
        db.AssetFamilies.Add(family);
        await db.SaveChangesAsync();
        var service = new AssetFamilyService(db);

        var kept = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Legacy", [F("Old-Key", "text") with { Help = "ok" }], 1, true),
            CancellationToken.None);
        Assert.Equal("Old-Key", kept.Fields.Single().Key);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest("Legacy", [F("Old-Key", "text"), F("New-Key", "text")], 1, true),
                CancellationToken.None));
    }

    [Fact]
    public async Task Type_change_cannot_hide_behind_the_archived_flag_in_a_two_step_save()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("weight", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("weight", "abc"));

        // Step 1: text -> number while archiving the field is checked like an active type change.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest("Fam", [F("weight", "number") with { Archived = true }], 1, true),
                CancellationToken.None));
        Assert.Contains("from text to number", ex.Message, StringComparison.Ordinal);

        // Archiving without a type change is fine; restoring or retyping it is blocked.
        await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest("Fam", [F("weight", "text") with { Archived = true }], 1, true),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest("Fam", [F("weight", "number")], 1, true),
                CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest("Fam", [F("weight", "number") with { Archived = true }], 1, true),
                CancellationToken.None));
    }

    [Fact]
    public async Task Adding_an_archived_field_over_incompatible_orphan_values_is_blocked()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("legacy_weight", "abc"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateAsync(
                family.Id,
                new UpdateAssetFamilyRequest(
                    "Fam",
                    [F("surface", "text"), F("legacy_weight", "number") with { Archived = true }],
                    1,
                    true),
                CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_archive_keeps_the_field_and_its_values()
    {
        var (db, service, family, tenants) = await ArrangeAsync([F("capacity", "number"), F("surface", "text")]);
        await using var _ = db;
        await SeedAssetAsync(db, tenants[0], family, "A1", ("capacity", 10));

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest(
                "Fam", [F("surface", "text"), F("capacity", "number") with { Archived = true }], 1, true),
            CancellationToken.None);

        Assert.True(updated.Fields.Single(f => f.Key == "capacity").Archived);
        Assert.Equal(2, updated.SchemaVersion);
    }

    [Fact]
    public async Task Round_trip_with_archived_fields_does_not_hit_the_active_field_limit()
    {
        var (db, service, family, _) = await ArrangeAsync([]);
        await using var _db = db;
        var fields = Enumerable.Range(0, 60).Select(i => F($"f{i}", "text"))
            .Concat(Enumerable.Range(0, 5).Select(i => F($"old{i}", "text") with { Archived = true }))
            .ToArray();

        var saved = await service.UpdateAsync(
            family.Id, new UpdateAssetFamilyRequest("Fam", fields, 1, true), CancellationToken.None);
        Assert.Equal(65, saved.Fields.Count);

        var again = await service.UpdateAsync(
            family.Id, new UpdateAssetFamilyRequest("Fam", saved.Fields, 1, true), CancellationToken.None);
        Assert.Equal(saved.SchemaVersion, again.SchemaVersion);

        var tooManyActive = Enumerable.Range(0, 61).Select(i => F($"g{i}", "text")).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                family.Id, new UpdateAssetFamilyRequest("Fam", tooManyActive, 1, true), CancellationToken.None));

        var tooManyTotal = Enumerable.Range(0, 201).Select(i => F($"h{i}", "text") with { Archived = true }).ToArray();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateAsync(
                family.Id, new UpdateAssetFamilyRequest("Fam", tooManyTotal, 1, true), CancellationToken.None));
    }

    [Fact]
    public async Task No_op_save_with_archived_fields_listed_last_does_not_bump_the_version()
    {
        await using var db = InMemoryAppDb.Create(new FakeTenantProvider { TenantId = null });
        var family = new AssetFamily
        {
            Key = "ordered",
            Label = "Ordered",
            FieldSchemaJson = """{"fields":[{"key":"old","type":"text","required":false,"label":"Old","archived":true},{"key":"surface","type":"text","required":false,"label":"Surface"}],"version":4}""",
            SortOrder = 1,
            IsActive = true,
        };
        db.AssetFamilies.Add(family);
        await db.SaveChangesAsync();
        var service = new AssetFamilyService(db);

        var updated = await service.UpdateAsync(
            family.Id,
            new UpdateAssetFamilyRequest(
                "Ordered",
                [
                    new AssetFamilyFieldDto("surface", "text", false, "Surface"),
                    new AssetFamilyFieldDto("old", "text", false, "Old", Archived: true),
                ],
                1,
                true,
                ExpectedSchemaVersion: 4),
            CancellationToken.None);

        Assert.Equal(4, updated.SchemaVersion);
        Assert.Equal(new[] { "old", "surface" }, updated.Fields.Select(f => f.Key).ToArray());
    }

    [Fact]
    public async Task Concurrent_schema_save_makes_the_stale_save_a_conflict()
    {
        var provider = new FakeTenantProvider { TenantId = null };
        var name = $"evolution-{Guid.NewGuid():N}";
        await using var first = InMemoryAppDb.Create(provider, name);
        await using var second = InMemoryAppDb.Create(provider, name);
        var created = await new AssetFamilyService(first).CreateAsync(
            new CreateAssetFamilyRequest("fam", "Fam", [F("surface", "text")], 1, true),
            CancellationToken.None);

        // The first context has already loaded (and tracks) the family at version 1.
        await first.AssetFamilies.SingleAsync(f => f.Id == created.Id);

        await new AssetFamilyService(second).UpdateAsync(
            created.Id,
            new UpdateAssetFamilyRequest("Fam", [F("surface", "text"), F("extra", "text")], 1, true),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AssetFamilyService(first).UpdateAsync(
                created.Id,
                new UpdateAssetFamilyRequest("Fam", [F("surface", "text"), F("other", "text")], 1, true),
                CancellationToken.None));
        Assert.Contains("changed since it was loaded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_after_a_concurrent_schema_save_is_a_conflict_not_a_server_error()
    {
        var provider = new FakeTenantProvider { TenantId = null };
        var name = $"evolution-del-{Guid.NewGuid():N}";
        await using var first = InMemoryAppDb.Create(provider, name);
        await using var second = InMemoryAppDb.Create(provider, name);
        var created = await new AssetFamilyService(first).CreateAsync(
            new CreateAssetFamilyRequest("fam", "Fam", [F("surface", "text")], 1, true),
            CancellationToken.None);
        await first.AssetFamilies.SingleAsync(f => f.Id == created.Id);

        await new AssetFamilyService(second).UpdateAsync(
            created.Id,
            new UpdateAssetFamilyRequest("Fam", [F("surface", "text"), F("extra", "text")], 1, true),
            CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AssetFamilyService(first).DeleteAsync(created.Id, CancellationToken.None));
        Assert.Contains("changed since it was loaded", ex.Message, StringComparison.Ordinal);
    }

    private static AssetFamilyFieldDto F(string key, string type) =>
        new(key, type, false, key);

    private static readonly ConditionalWeakTable<AppDbContext, FakeTenantProvider> ProviderByDb = new();

    private static FakeTenantProvider TenantProvider(AppDbContext db) =>
        ProviderByDb.TryGetValue(db, out var provider)
            ? provider
            : throw new InvalidOperationException("Unknown test database.");

    private static async Task<(AppDbContext Db, AssetFamilyService Service, AssetFamilyDetailResponse Family, Tenant[] Tenants)>
        ArrangeAsync(IReadOnlyList<AssetFamilyFieldDto> fields)
    {
        var provider = new FakeTenantProvider { TenantId = null };
        var db = InMemoryAppDb.Create(provider);
        ProviderByDb.Add(db, provider);
        var service = new AssetFamilyService(db);
        var family = await service.CreateAsync(
            new CreateAssetFamilyRequest("fam", "Fam", fields, 1, true),
            CancellationToken.None);

        var tenants = new[]
        {
            new Tenant("Tenant A", "12345678000101", subdomain: "tenant-a-evolution"),
            new Tenant("Tenant B", "12345678000102", subdomain: "tenant-b-evolution"),
        };
        db.Tenants.AddRange(tenants);
        foreach (var tenant in tenants)
        {
            db.Units.Add(new Unit(tenant.Id, "Matriz"));
            db.AssetCategories.Add(new AssetCategory { TenantId = tenant.Id, Name = "Categoria" });
        }

        await db.SaveChangesAsync();
        return (db, service, family, tenants);
    }

    private static async Task<Asset> SeedAssetAsync(
        AppDbContext db,
        Tenant tenant,
        AssetFamilyDetailResponse family,
        string tag,
        params (string Key, object Value)[] attributes)
    {
        var unit = await db.Units.IgnoreQueryFilters().FirstAsync(u => u.TenantId == tenant.Id);
        var category = await db.AssetCategories.IgnoreQueryFilters().FirstAsync(c => c.TenantId == tenant.Id);
        var asset = new Asset
        {
            TenantId = tenant.Id,
            UnitId = unit.Id,
            CategoryId = category.Id,
            FamilyId = family.Id,
            Name = tag,
            Tag = tag,
            Status = AssetStatus.Active,
            Attributes = attributes.ToDictionary(
                a => a.Key,
                a => JsonSerializer.SerializeToElement(a.Value),
                StringComparer.Ordinal),
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
