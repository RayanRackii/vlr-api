using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Serialization;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Api.Tests.Assets;

public sealed class AssetVisualAssignmentTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void Update_JSON_distinguishes_omitted_revision_and_capacity_from_explicit_null()
    {
        var json = JsonSerializer.Serialize(new { name = "Resource", tag = "R", unitId = Guid.NewGuid(), categoryId = Guid.NewGuid(), familyId = Guid.NewGuid() });
        var omitted = JsonSerializer.Deserialize<UpdateRentableRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.False(omitted.VisualTemplateRevisionId.IsSpecified);
        Assert.False(omitted.CapacityPeople.IsSpecified);
        var serializedOmitted = JsonSerializer.Serialize(omitted, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("visualTemplateRevisionId", serializedOmitted);
        Assert.DoesNotContain("capacityPeople", serializedOmitted);
        var cleared = JsonSerializer.Deserialize<UpdateRentableRequest>(json[..^1] + ",\"visualTemplateRevisionId\":null,\"capacityPeople\":null}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(cleared.VisualTemplateRevisionId.IsSpecified);
        Assert.Null(cleared.VisualTemplateRevisionId.Value);
        Assert.True(cleared.CapacityPeople.IsSpecified);
        Assert.Null(cleared.CapacityPeople.Value);
    }

    [Fact]
    public void Guid_optional_converter_reads_and_writes_a_UUID_without_presence_metadata()
    {
        var id = Guid.NewGuid();
        var options = new JsonSerializerOptions();
        options.Converters.Add(new JsonOptionalGuidConverter());
        var parsed = JsonSerializer.Deserialize<JsonOptional<Guid>>($"\"{id}\"", options);
        Assert.True(parsed.IsSpecified);
        Assert.Equal(id, parsed.Value);
        Assert.Equal($"\"{id}\"", JsonSerializer.Serialize(parsed, options));
        Assert.Equal("null", JsonSerializer.Serialize(JsonOptional<Guid>.Specified(null), options));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("draft")]
    [InlineData("archived")]
    public async Task Invalid_choice_does_not_create_asset_or_rental_configuration(string choice)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var revision = await SeedVisualAsync(h, active: choice != "archived", published: choice != "draft");
        var error = await Assert.ThrowsAsync<ResourceVisualException>(() => h.CreateRegistry().CreateRentableAsync(
            Create(h) with { VisualTemplateRevisionId = choice == "missing" ? Guid.NewGuid() : revision.Id }, Ct));
        Assert.Equal(choice == "archived" ? "VISUAL_TEMPLATE_ARCHIVED" : "VISUAL_TEMPLATE_INVALID", error.Code);
        Assert.Empty(h.Db.Assets);
        Assert.Empty(h.Db.RentalAssets);
    }

    [Fact]
    public async Task Published_choice_and_capacity_are_saved_with_registry_and_exposed_by_both_DTOs()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var revision = await SeedVisualAsync(h);
        var response = await h.CreateService().CreateRentableAsync(Create(h) with
        { VisualTemplateRevisionId = revision.Id, CapacityPeople = 6, MaxAdvanceBookingDays = 17 }, Ct);
        Assert.Equal(revision.Id, response.RentalConfig!.VisualTemplateRevisionId);
        Assert.Equal(6, response.RentalConfig.CapacityPeople);
        var rental = await new RentalAssetService(h.Db, h.TenantProvider).GetByAssetIdAsync(response.Id, Ct);
        Assert.NotNull(rental);
        Assert.Equal(revision.Id, rental.VisualTemplateRevisionId);
        Assert.Equal(6, rental.CapacityPeople);
        Assert.Equal(17, rental.MaxAdvanceBookingDays);
        Assert.Equal(1, rental.TotalQuantity);
    }

    [Fact]
    public async Task Legacy_requests_keep_generic_visual_and_omitted_updates_preserve_assignments()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var registry = h.CreateRegistry();
        var resource = await registry.CreateRentableAsync(Create(h), Ct);
        Assert.Null(resource.RentalConfiguration!.VisualTemplateRevisionId);
        Assert.Null(resource.RentalConfiguration.CapacityPeople);
        var revision = await SeedVisualAsync(h);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with
        { VisualTemplateRevisionId = revision.Id, CapacityPeople = 12, MaxAdvanceBookingDays = 8 }, Ct);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h), Ct);
        Assert.Equal(revision.Id, resource.RentalConfiguration.VisualTemplateRevisionId);
        Assert.Equal(12, resource.RentalConfiguration.CapacityPeople);
        Assert.Equal(8, resource.RentalConfiguration.MaxAdvanceBookingDays);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with
        { VisualTemplateRevisionId = JsonOptional<Guid>.Specified(null), CapacityPeople = JsonOptional<int>.Specified(null), MaxAdvanceBookingDays = JsonOptional<int>.Specified(null) }, Ct);
        Assert.Null(resource.RentalConfiguration.VisualTemplateRevisionId);
        Assert.Null(resource.RentalConfiguration.CapacityPeople);
        Assert.Null(resource.RentalConfiguration.MaxAdvanceBookingDays);
    }

    [Fact]
    public async Task Archived_existing_revision_is_retained_but_new_archived_choice_is_rejected()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var revision = await SeedVisualAsync(h);
        var registry = h.CreateRegistry();
        var resource = await registry.CreateRentableAsync(Create(h) with { VisualTemplateRevisionId = revision.Id }, Ct);
        var template = await h.Db.Set<ResourceVisualTemplate>().SingleAsync(x => x.Id == revision.TemplateId);
        template.IsActive = false;
        await h.Db.SaveChangesAsync();
        await registry.UpdateRentableAsync(resource.RentalConfiguration!.Id, Update(h), Ct);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { VisualTemplateRevisionId = revision.Id }, Ct);
        Assert.Equal(revision.Id, resource.RentalConfiguration.VisualTemplateRevisionId);
        var other = await SeedVisualAsync(h, active: false);
        var error = await Assert.ThrowsAsync<ResourceVisualException>(() => registry.UpdateRentableAsync(resource.RentalConfiguration.Id,
            Update(h) with { Name = "Should not save", VisualTemplateRevisionId = other.Id }, Ct));
        Assert.Equal("VISUAL_TEMPLATE_ARCHIVED", error.Code);
        Assert.Equal("Resource", resource.Name);
        Assert.Equal(revision.Id, resource.RentalConfiguration.VisualTemplateRevisionId);
    }

    [Fact]
    public async Task Suggestions_require_confirmation_and_do_not_change_schedule_or_overwrite_capacity()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var revision = await SeedVisualAsync(h, suggestedCapacity: 4);
        var registry = h.CreateRegistry();
        var resource = await registry.CreateRentableAsync(Create(h) with { VisualTemplateRevisionId = revision.Id }, Ct);
        Assert.Null(resource.RentalConfiguration!.CapacityPeople);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { ConfirmVisualSuggestions = true }, Ct);
        Assert.Equal(4, resource.RentalConfiguration.CapacityPeople);
        Assert.Null(resource.RentalConfiguration.AllowedDurationMinutes);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { CapacityPeople = 9, ConfirmVisualSuggestions = true }, Ct);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { ConfirmVisualSuggestions = true }, Ct);
        Assert.Equal(9, resource.RentalConfiguration.CapacityPeople);
        await registry.UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { CapacityPeople = JsonOptional<int>.Specified(null), ConfirmVisualSuggestions = true }, Ct);
        Assert.Null(resource.RentalConfiguration.CapacityPeople);
    }

    [Fact]
    public async Task Confirmed_create_can_use_suggested_capacity_without_changing_stock()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var revision = await SeedVisualAsync(h, suggestedCapacity: 4);
        var resource = await h.CreateRegistry().CreateRentableAsync(Create(h) with { VisualTemplateRevisionId = revision.Id, ConfirmVisualSuggestions = true }, Ct);
        Assert.Equal(4, resource.RentalConfiguration!.CapacityPeople);
        Assert.Equal(1, resource.RentalConfiguration.TotalQuantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100001)]
    public async Task Invalid_capacity_rejects_before_any_registry_write(int capacity)
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        await Assert.ThrowsAsync<ResourceVisualException>(() => h.CreateRegistry().CreateRentableAsync(Create(h) with { CapacityPeople = capacity }, Ct));
        Assert.Empty(h.Db.Assets);
        Assert.Empty(h.Db.RentalAssets);
    }

    [Fact]
    public async Task Moving_a_resource_outside_its_map_unit_rejects_before_mutating_asset()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var resource = await h.CreateRegistry().CreateRentableAsync(Create(h), Ct);
        var unit = new Unit(h.TenantProvider.TenantId!.Value, "Other site");
        var map = new RentalLayout { TenantId = resource.TenantId, UnitId = h.UnitId, Name = "Scoped map", IsActive = true, AspectRatio = 1.6, WidthPercent = 100 };
        map.AddItem(new RentalLayoutItem { TenantId = resource.TenantId, LayoutId = map.Id, RentalAssetId = resource.RentalConfiguration!.Id, XPercent = 0, YPercent = 0, WidthPercent = 20, HeightPercent = 20, ZIndex = 0 });
        h.Db.AddRange(unit, map); await h.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ResourceVisualException>(() => h.CreateRegistry().UpdateRentableAsync(resource.RentalConfiguration.Id, Update(h) with { UnitId = unit.Id, Name = "Should not save" }, Ct));
        Assert.Equal("MAP_INVALID", error.Code);
        Assert.Equal(h.UnitId, resource.UnitId);
        Assert.Equal("Resource", resource.Name);
    }

    [Fact]
    public async Task Empty_tenant_is_rejected_and_foreign_resource_update_does_not_cross_scope()
    {
        await using var h = await BulkCreateAssetsHarness.CreateAsync();
        var resource = await h.CreateRegistry().CreateRentableAsync(Create(h), Ct);
        var ownerTenant = h.TenantProvider.TenantId;
        h.TenantProvider.TenantId = Guid.NewGuid();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.CreateRegistry().UpdateRentableAsync(resource.RentalConfiguration!.Id, Update(h), Ct));
        Assert.Null(await h.CreateService().GetByIdAsync(resource.Id, Ct));
        h.TenantProvider.TenantId = Guid.Empty;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.CreateService().ListAsync(Ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.CreateRegistry().CreateRentableAsync(Create(h), Ct));
        h.TenantProvider.TenantId = ownerTenant;
        Assert.Single(h.Db.Assets);
    }

    private static CreateRentableRequest Create(BulkCreateAssetsHarness h) => new()
    { Name = "Resource", Tag = "R", UnitId = h.UnitId, CategoryId = h.CategoryId, FamilyId = h.FamilyId };
    private static UpdateRentableRequest Update(BulkCreateAssetsHarness h) => new()
    { Name = "Resource", Tag = "R", UnitId = h.UnitId, CategoryId = h.CategoryId, FamilyId = h.FamilyId };

    private static async Task<ResourceVisualTemplateRevision> SeedVisualAsync(BulkCreateAssetsHarness h, bool active = true, bool published = true, int? suggestedCapacity = null)
    {
        var template = new ResourceVisualTemplate { Slug = "test-" + Guid.NewGuid().ToString("N"), IsActive = active, NameI18n = new() { ["en"] = "Test" } };
        var revision = new ResourceVisualTemplateRevision { TemplateId = template.Id, RevisionNo = 1, FloorSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#AABBCC\"/></svg>", SuggestedCapacityPeople = suggestedCapacity };
        if (published) ResourceVisualArt.PreparePublication(revision, DateTimeOffset.UtcNow);
        h.Db.AddRange(template, revision); await h.Db.SaveChangesAsync(); return revision;
    }
}
