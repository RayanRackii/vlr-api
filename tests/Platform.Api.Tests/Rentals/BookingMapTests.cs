using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Rentals;

public sealed class BookingMapTests
{
    internal static RentalLayoutService Service(AvailabilityHarness h) => new(h.Db, h.TenantProvider, new FakeTrialGuard());
    internal static UpsertRentalLayoutRequestDto Legacy(AvailabilityHarness h) => new()
    { Name = "Main area", UnitId = h.Unit.Id, Items = [new() { RentalAssetId = h.Primary.Id,
        XPercent = 5, YPercent = 10, WidthPercent = 30, HeightPercent = 40 }] };
    internal static SaveBookingMapCompositionRequest Composition(BookingMapCompositionDto map) => new()
    {
        ExpectedVersion = map.Version, Name = map.Name, UnitId = map.UnitId, IsActive = map.IsActive,
        SortOrder = map.SortOrder, AspectRatio = map.AspectRatio, WidthPercent = map.WidthPercent,
        Items = map.Items.Select(i => new SaveBookingMapItemRequest { Id = i.Id, RentalAssetId = i.RentalAssetId,
            XPercent = i.XPercent, YPercent = i.YPercent, WidthPercent = i.WidthPercent, HeightPercent = i.HeightPercent,
            Rotation = i.Rotation, LockAspectRatio = i.LockAspectRatio, ZIndex = i.ZIndex }).ToArray()
    };
    internal static SaveBookingMapSettingsRequest Settings(long version, Guid? layoutId = null) => new()
    { ExpectedVersion = version, DefaultLayoutId = layoutId, AvailableColor = "#15803d", TakenColor = "#B91C1C",
        UnavailableColor = "#6B7280", SelectedColor = "#1D4ED8" };
    internal static SaveBookingMapDecorationRequest Decoration(string style = "{}") => new()
    { Kind = "Walkway", Label = "Entrance", XPercent = 0, YPercent = 80, WidthPercent = 100,
        HeightPercent = 10, Style = JsonSerializer.Deserialize<JsonElement>(style) };

    [Fact]
    public async Task Composition_is_atomic_versioned_and_legacy_update_keeps_ids_rotation_decorations()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var service = Service(h);
        var created = await service.CreateAsync(Legacy(h), default);
        var initial = (await service.GetCompositionAsync(created.Id, default))!;
        var request = Composition(initial) with { SortOrder = 7, Items = [Composition(initial).Items[0] with { Rotation = 90, LockAspectRatio = false }],
            Decorations = [Decoration("{\"fill\":\"#123456\",\"opacity\":0.4}")] };
        var saved = await service.SaveCompositionAsync(created.Id, request, default);
        Assert.Equal(2, saved.Version);
        Assert.Equal(initial.Items[0].Id, saved.Items[0].Id);
        var conflict = await Assert.ThrowsAsync<BookingMapException>(() => service.SaveCompositionAsync(created.Id, request, default));
        Assert.Equal("MAP_VERSION_CONFLICT", conflict.Code);
        await service.UpdateAsync(created.Id, Legacy(h) with { Name = "Renamed" }, default);
        var legacy = (await service.GetCompositionAsync(created.Id, default))!;
        Assert.Equal(3, legacy.Version);
        Assert.Equal(7, legacy.SortOrder);
        Assert.Equal(initial.Items[0].Id, legacy.Items[0].Id);
        Assert.Equal(90, legacy.Items[0].Rotation);
        Assert.False(legacy.Items[0].LockAspectRatio);
        Assert.Single(legacy.Decorations);
        Assert.Equal(saved.Decorations[0].Id, legacy.Decorations[0].Id);
    }

    [Fact]
    public async Task Foreign_unit_resource_and_item_ids_are_rejected_without_changing_composition()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var foreign = new Tenant("Other", "22222222000191", subdomain: "other");
        var otherUnit = new Unit(foreign.Id, "Private"); h.Db.AddRange(foreign, otherUnit);
        var other = await h.AddResourceAsync(foreign, otherUnit, "Private resource");
        var service = Service(h);
        var own = await service.CreateAsync(Legacy(h), default);
        var map = (await service.GetCompositionAsync(own.Id, default))!;
        var cases = new[] { Composition(map) with { UnitId = otherUnit.Id },
            Composition(map) with { Items = [Composition(map).Items[0] with { RentalAssetId = other.Id }] },
            Composition(map) with { Items = [Composition(map).Items[0] with { Id = Guid.NewGuid() }] } };
        foreach (var request in cases)
        {
            Assert.Equal("MAP_INVALID", (await Assert.ThrowsAsync<BookingMapException>(() => service.SaveCompositionAsync(map.Id, request, default))).Code);
            Assert.Equal(1, (await service.GetCompositionAsync(map.Id, default))!.Version);
        }
        h.TenantProvider.TenantId = foreign.Id;
        Assert.Null(await service.GetCompositionAsync(own.Id, default));
        Assert.Null(await service.GetPublicMapAsync(own.Id, null, default));
        Assert.Empty((await service.ListPublicMapsAsync(null, default)).Maps);
    }

    [Fact]
    public async Task Unit_specific_maps_reject_other_units_from_the_same_tenant()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var unit = new Unit(h.Tenant.Id, "Another unit"); h.Db.Add(unit); await h.Db.SaveChangesAsync();
        var service = Service(h);
        Assert.Equal("MAP_INVALID", (await Assert.ThrowsAsync<BookingMapException>(() => service.CreateAsync(Legacy(h) with { UnitId = unit.Id }, default))).Code);
        Assert.Empty(await h.Db.RentalLayouts.ToListAsync());
    }

    [Theory]
    [InlineData(-1, 0, 20, 20, 0)] [InlineData(90, 0, 20, 20, 0)]
    [InlineData(0, 0, 0, 20, 0)] [InlineData(0, 0, 20, 20, 45)]
    [InlineData(double.NaN, 0, 20, 20, 0)] [InlineData(0, 0, double.PositiveInfinity, 20, 0)]
    public void Invalid_geometry_never_gets_clamped(double x, double y, double width, double height, short rotation) =>
        Assert.Equal("MAP_INVALID", Assert.Throws<BookingMapException>(() => BookingMapValidation.Rectangle(x, y, width, height, rotation)).Code);

    [Theory]
    [InlineData("{\"fill\":\"url(https://host/x)\"}")]
    [InlineData("{\"html\":\"<script>alert(1)</script>\"}")]
    [InlineData("{\"style\":\"position:fixed\"}")]
    [InlineData("{\"strokeWidth\":\"1\"}")]
    [InlineData("{\"opacity\":2}")]
    [InlineData("{\"fill\":\"#123456\",\"fill\":\"#654321\"}")]
    public void Decoration_rejects_code_urls_unknown_css_and_invalid_numbers(string style) =>
        Assert.Equal("MAP_INVALID", Assert.Throws<BookingMapException>(() => BookingMapValidation.Decoration(Decoration(style))).Code);

    [Fact]
    public void Decoration_geometry_and_label_are_bounded_plain_data()
    {
        var bad = Decoration() with { Geometry = JsonSerializer.Deserialize<JsonElement>("{\"points\":[{\"x\":0,\"y\":0},{\"x\":200,\"y\":1}]}") };
        Assert.Throws<BookingMapException>(() => BookingMapValidation.Decoration(bad));
        Assert.Throws<BookingMapException>(() => BookingMapValidation.Decoration(Decoration() with { Label = "<iframe>" }));
        Assert.Throws<BookingMapException>(() => BookingMapValidation.Decoration(Decoration() with { IconKey = "https://host/a.svg" }));
        Assert.Throws<BookingMapException>(() => BookingMapValidation.Decoration(Decoration() with { Label = new string('a', 121) }));
    }

    [Fact]
    public async Task Invalid_decoration_is_validated_before_any_item_is_modified()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = Service(h);
        var created = await service.CreateAsync(Legacy(h), default);
        var map = (await service.GetCompositionAsync(created.Id, default))!;
        var request = Composition(map) with { Name = "Must not persist", Items = [Composition(map).Items[0] with { XPercent = 20 }],
            Decorations = [Decoration("{\"onload\":\"evil\"}")] };
        await Assert.ThrowsAsync<BookingMapException>(() => service.SaveCompositionAsync(map.Id, request, default));
        var unchanged = (await service.GetCompositionAsync(map.Id, default))!;
        Assert.Equal(map.Name, unchanged.Name); Assert.Equal(5, unchanged.Items[0].XPercent); Assert.Equal(1, unchanged.Version);
        Assert.DoesNotContain(h.Db.ChangeTracker.Entries(), e => e.State is EntityState.Modified or EntityState.Added or EntityState.Deleted);
    }

    [Fact]
    public async Task Settings_versions_defaults_and_map_deletion_preserve_real_resources()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = Service(h);
        Assert.Equal(0, (await service.GetSettingsAsync(default)).Version);
        Assert.Empty(await h.Db.Set<BookingMapSettings>().ToListAsync());
        Assert.Equal("List", (await service.ListPublicMapsAsync(null, default)).Settings.DefaultView);
        var map = await service.CreateAsync(Legacy(h), default);
        var settings = await service.SaveSettingsAsync(Settings(0, map.Id), default);
        Assert.Equal(1, settings.Version); Assert.Equal("#15803D", settings.AvailableColor);
        Assert.Equal(map.Id, (await service.ListPublicMapsAsync(null, default)).DefaultLayoutId);
        Assert.Equal("MAP_VERSION_CONFLICT", (await Assert.ThrowsAsync<BookingMapException>(() => service.SaveSettingsAsync(Settings(0), default))).Code);
        await service.DeleteAsync(map.Id, default);
        var cleared = await service.GetSettingsAsync(default);
        Assert.Null(cleared.DefaultLayoutId); Assert.Equal(2, cleared.Version);
        Assert.Single(await h.Db.RentalAssets.ToListAsync());
    }

    [Fact]
    public async Task Public_projection_deduplicates_only_referenced_published_art_and_filters_inactive_resources()
    {
        await using var h = await AvailabilityHarness.CreateAsync();
        var second = await h.AddResourceAsync(h.Tenant, h.Unit, "Another visible resource");
        var template = new ResourceVisualTemplate { Slug = "sample", IsActive = false };
        var published = new ResourceVisualTemplateRevision { TemplateId = template.Id, RevisionNo = 1,
            Status = ResourceVisualRevisionStatus.Published, FloorSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 1 1\"/>", ArtSha256 = new string('a', 64) };
        var unused = new ResourceVisualTemplateRevision { TemplateId = template.Id, RevisionNo = 2, Status = ResourceVisualRevisionStatus.Draft, FloorSvg = "PRIVATE DRAFT" };
        h.Db.AddRange(template, published, unused);
        h.Primary.VisualTemplateRevisionId = published.Id; second.VisualTemplateRevisionId = published.Id;
        await h.Db.SaveChangesAsync();
        var service = Service(h);
        var map = await service.CreateAsync(Legacy(h) with { Items = [Legacy(h).Items[0], Legacy(h).Items[0] with { RentalAssetId = second.Id, XPercent = 60 }] }, default);
        var publicMap = (await service.GetPublicMapAsync(map.Id, null, default))!;
        Assert.Equal(2, publicMap.Items.Count); Assert.Single(publicMap.VisualRevisions);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(publicMap));
        h.Primary.IsActive = false; await h.Db.SaveChangesAsync();
        var activeOnly = (await service.GetPublicMapAsync(map.Id, null, default))!;
        Assert.Single(activeOnly.Items); Assert.DoesNotContain(h.Primary.Id, activeOnly.Items.Select(i => i.RentalAssetId));
        Assert.Null(await service.GetPublicMapAsync(map.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Demo_manifest_is_idempotent_reuses_resources_and_never_changes_real_art_or_existing_maps()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = Service(h);
        var original = await service.CreateAsync(Legacy(h), default);
        var count = await h.Db.RentalAssets.CountAsync();
        var request = new SeedDemoBookingMapRequest { UnitId = h.Unit.Id,
            Items = [new() { RentalAssetId = h.Primary.Id, XPercent = 10, YPercent = 10, WidthPercent = 30, HeightPercent = 40 }],
            Decorations = [Decoration()] };
        var seeded = await service.SeedDemoAsync(request, default);
        var repeated = await service.SeedDemoAsync(request with { Name = "Must not overwrite" }, default);
        Assert.Equal(seeded.Id, repeated.Id); Assert.Equal(seeded.Name, repeated.Name); Assert.Equal(1, repeated.Version);
        Assert.NotEqual(original.Id, seeded.Id); Assert.Equal(count, await h.Db.RentalAssets.CountAsync());
        Assert.Null(h.Primary.VisualTemplateRevisionId); Assert.Equal(2, await h.Db.RentalLayouts.CountAsync());
        Assert.Empty(await h.Db.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Database_concurrency_token_rejects_an_editor_that_loaded_before_another_commit()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = Service(h);
        var created = await service.CreateAsync(Legacy(h), default);
        var initial = (await service.GetCompositionAsync(created.Id, default))!;
        await using var otherDb = InMemoryAppDb.Create(h.TenantProvider, h.DatabaseName);
        await otherDb.RentalLayouts.SingleAsync(m => m.Id == created.Id); // Track version 1 before the first save.
        var otherService = new RentalLayoutService(otherDb, h.TenantProvider, new FakeTrialGuard());
        await service.SaveCompositionAsync(created.Id, Composition(initial) with { Name = "First editor" }, default);
        var conflict = await Assert.ThrowsAsync<BookingMapException>(() => otherService.SaveCompositionAsync(created.Id,
            Composition(initial) with { Name = "Stale editor" }, default));
        Assert.Equal("MAP_VERSION_CONFLICT", conflict.Code);
        Assert.Equal("First editor", (await service.GetCompositionAsync(created.Id, default))!.Name);
    }

    [Fact]
    public async Task Empty_tenant_never_disables_isolation()
    {
        await using var h = await AvailabilityHarness.CreateAsync(); var service = Service(h);
        foreach (var tenantId in new Guid?[] { null, Guid.Empty })
        {
            h.TenantProvider.TenantId = tenantId;
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCompositionAsync(Guid.NewGuid(), default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListPublicMapsAsync(null, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetSettingsAsync(default));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => BookingMapLocks.AcquireAsync(h.Db, Guid.Empty, default));
    }
}
