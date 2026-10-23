using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Core.Domain.Enums;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.ResourceVisuals;

public sealed class ResourceVisualCatalogTests
{
    private static ResourceVisualCatalogService Service(AppDbContext db, FakeTenantProvider tenant) => new(db, tenant, new FakeTrialGuard(), new Platform.Api.Modules.Rentals.Services.RentalVisualDefaults(), NullLogger<ResourceVisualCatalogService>.Instance);
    private static async Task Seed(AppDbContext db)
    {
        db.AddRange(ResourceVisualCatalogSeed.Tags); db.AddRange(ResourceVisualCatalogSeed.Templates); db.AddRange(ResourceVisualCatalogSeed.Revisions); db.AddRange(ResourceVisualCatalogSeed.TemplateTags); await db.SaveChangesAsync();
    }
    private static Guid Tag(string slug) => ResourceVisualCatalogSeed.StableId("tag:" + slug);
    [Fact]
    public async Task Picker_defaults_to_only_current_tenant_segments_and_all_removes_defaults()
    {
        var a = new Tenant("A", "a"); var b = new Tenant("B", "b"); var tenant = new FakeTenantProvider { TenantId = a.Id };
        await using var db = InMemoryAppDb.Create(tenant); db.AddRange(a, b); await Seed(db);
        db.AddRange(new TenantResourceSegment { TenantId = a.Id, TagId = Tag("sports-courts") }, new TenantResourceSegment { TenantId = b.Id, TagId = Tag("health") }); await db.SaveChangesAsync();
        var service = Service(db, tenant);
        Assert.Equal(7, (await service.ListAsync(new(null, null, null), true, false, default)).Total);
        Assert.Equal(13, (await service.ListAsync(new(null, null, null, AllSegments: true), true, false, default)).Total);
        tenant.TenantId = b.Id;
        Assert.Equal("consultation-room", Assert.Single((await service.ListAsync(new(null, null, null), true, false, default)).Items).Slug);
    }
    [Fact]
    public async Task Filters_OR_within_facet_AND_between_facets_and_text_terms()
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() }; await using var db = InMemoryAppDb.Create(tenant); await Seed(db); var service = Service(db, tenant);
        var query = new VisualPickerQuery(null, [Tag("sports-courts")], [Tag("sand"), Tag("clay"), Tag("tennis")]);
        Assert.Equal("tennis-clay", Assert.Single((await service.ListAsync(query, true, false, default)).Items).Slug);
        Assert.Equal(0, (await service.ListAsync(query with { Search = "tennis absent" }, true, false, default)).Total);
        Assert.Equal(1, (await service.ListAsync(query with { Search = "tennis clay" }, true, false, default)).Total);
    }
    [Fact]
    public async Task Missing_tenant_never_expands_discovery_scope()
    {
        var tenant = new FakeTenantProvider(); await using var db = InMemoryAppDb.Create(tenant); var service = Service(db, tenant);
        Assert.Equal("TENANT_REQUIRED", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.ListAsync(new(null, null, null), true, false, default))).Code);
        Assert.Equal("TENANT_REQUIRED", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.SegmentsAsync(null, default))).Code);
        Assert.Equal("TENANT_REQUIRED", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.TagsAsync(true, default))).Code);
    }
    [Fact]
    public async Task Segments_are_replaceable_preferences_without_modifying_other_tenants()
    {
        var a = new Tenant("A", "a"); var b = new Tenant("B", "b"); var tenant = new FakeTenantProvider { TenantId = a.Id }; await using var db = InMemoryAppDb.Create(tenant); db.AddRange(a, b); await Seed(db); var service = Service(db, tenant);
        await service.ReplaceSegmentsAsync(b.Id, new([Tag("health")]), default);
        await service.ReplaceSegmentsAsync(null, new([Tag("sports-courts")]), default);
        Assert.Equal(Tag("health"), Assert.Single(await service.SegmentsAsync(b.Id, default)).Id);
        Assert.Equal("SEGMENT_INVALID", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.ReplaceSegmentsAsync(null, new([Tag("sand")]), default))).Code);
    }
    [Fact]
    public async Task Published_art_remains_accessible_when_archived_and_cannot_be_edited_or_deleted()
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() }; await using var db = InMemoryAppDb.Create(tenant); await Seed(db); var service = Service(db, tenant);
        var revision = ResourceVisualCatalogSeed.Revisions[0]; await service.ArchiveAsync(revision.TemplateId, default);
        Assert.NotNull(await service.PublishedArtAsync(revision.Id, default));
        Assert.DoesNotContain((await service.ListAsync(new(null, null, null), true, false, default)).Items, x => x.Id == revision.TemplateId);
        var tracked = await db.Set<ResourceVisualTemplateRevision>().SingleAsync(x => x.Id == revision.Id); tracked.DefaultColor = "#000000";
        Assert.Throws<InvalidOperationException>(() => ResourceVisualRevisionImmutability.Ensure(db.ChangeTracker));
        db.Entry(tracked).State = EntityState.Unchanged; db.Remove(tracked);
        Assert.Throws<InvalidOperationException>(() => ResourceVisualRevisionImmutability.Ensure(db.ChangeTracker));
    }
    [Fact]
    public async Task Draft_publication_sanitizes_once_and_a_new_revision_preserves_original()
    {
        var tenant = new FakeTenantProvider(); await using var db = InMemoryAppDb.Create(tenant); var service = Service(db, tenant);
        var template = await service.SaveTemplateAsync(null, new("test-room", new() { ["en"] = "Room" }, [], []), default);
        var request = new RevisionRequest("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#ABCDEF\"/></svg>", null, null, "0 0 100 100", 1, "#ABCDEF", "square");
        var draft = await service.SaveRevisionAsync(template.Id, null, request, default);
        Assert.Null(await service.PublishedArtAsync(draft.Id, default));
        var published = await service.PublishAsync(template.Id, draft.Id, default); Assert.NotNull(published.ArtUrl);
        var error = await Assert.ThrowsAsync<ResourceVisualException>(() => service.SaveRevisionAsync(template.Id, draft.Id, request, default)); Assert.Equal(409, error.Status);
        var second = await service.SaveRevisionAsync(template.Id, null, request, default); Assert.Equal(2, second.RevisionNo); Assert.Equal(published.ArtSha256, (await service.PublishedArtAsync(draft.Id, default))!.ArtSha256);
    }
    [Fact]
    public async Task Open_hours_default_durations_are_validated_and_normalized()
    {
        var tenant = new FakeTenantProvider(); await using var db = InMemoryAppDb.Create(tenant); var service = Service(db, tenant);
        var template = await service.SaveTemplateAsync(null, new("duration-room", new() { ["en"] = "Room" }, [], []), default);
        var request = new RevisionRequest("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#ABCDEF\"/></svg>", null, null, "0 0 100 100", 1, "#ABCDEF", "square",
            DefaultSchedulePolicy: SchedulePolicy.OpenHours,
            DefaultOpenTime: new(8, 0),
            DefaultCloseTime: new(22, 0),
            DefaultAllowedDurationMinutes: " 120, 60, 120 ");

        var revision = await service.SaveRevisionAsync(template.Id, null, request, default);

        Assert.Equal("60,120", revision.DefaultAllowedDurationMinutes);
        var invalid = request with { DefaultAllowedDurationMinutes = "60, nope" };
        Assert.Equal("VISUAL_TEMPLATE_INVALID", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.SaveRevisionAsync(template.Id, revision.Id, invalid, default))).Code);
    }
    [Fact]
    public async Task Inherited_archived_segment_does_not_break_picker_but_explicit_archived_filter_is_rejected()
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() }; await using var db = InMemoryAppDb.Create(tenant); await Seed(db);
        db.Add(new TenantResourceSegment { TenantId = tenant.TenantId.Value, TagId = Tag("sports-courts") });
        var tag = await db.Set<ResourceVisualTag>().SingleAsync(x => x.Id == Tag("sports-courts")); tag.IsActive = false; await db.SaveChangesAsync();
        var service = Service(db, tenant); Assert.Equal(13, (await service.ListAsync(new(null, null, null), true, false, default)).Total);
        Assert.Equal("SEGMENT_INVALID", (await Assert.ThrowsAsync<ResourceVisualException>(() => service.ListAsync(new(null, [tag.Id], null), true, false, default))).Code);
    }
}
