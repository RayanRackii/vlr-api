using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Persistence.Seed;

namespace Platform.Api.Tests.ResourceVisuals;

// A separate class fixture owns this database: rolling back its schema must never
// affect the databases used by concurrent service tests.
public sealed class ResourceVisualPostgresMigrationTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private const string LegacyMigration = "20261020220848_AddPlatformTaxonomyAndCatalogAssignments";
    private const string MapMigration = "20261021231855_ExtendRentalMapsAndVisualAssignments";
    private const string SeedMigration = "20261021231941_SeedResourceVisualCatalogV1";
    private const string LatestMigration = "20261021232049_AddBookingMapPermissions";

    [DockerFact]
    public async Task Up_down_up_preserves_legacy_rows_and_seed_rollback_refuses_references_and_modified_metadata()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        Sentinel sentinel;
        await using (var seed = factory.Create(provider))
            sentinel = await SeedLegacyRowsAsync(seed, provider);

        var original = await LegacySnapshotAsync(factory, provider, sentinel);
        await MigrateAsync(factory, provider, LegacyMigration);
        Assert.Equal(original, await LegacySnapshotAsync(factory, provider, sentinel));

        await MigrateAsync(factory, provider, LatestMigration);
        Assert.Equal(original, await LegacySnapshotAsync(factory, provider, sentinel));
        await AssertCatalogAsync(factory, provider, sentinel.AdminRoleId);

        var revisionId = ResourceVisualCatalogSeed.StableId("revision:beach-tennis:1");
        await using (var db = factory.Create(provider))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rentals.rental_assets SET visual_template_revision_id = {revisionId} WHERE id = {sentinel.RentalId}");
        await AssertSeedRollbackBlockedAsync(factory, provider, "referenced or extended");
        await using (var db = factory.Create(provider))
        {
            Assert.Equal(revisionId, await db.RentalAssets.Where(r => r.Id == sentinel.RentalId).Select(r => r.VisualTemplateRevisionId).SingleAsync());
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE rentals.rental_assets SET visual_template_revision_id = NULL WHERE id = {sentinel.RentalId}");
        }
        await MigrateAsync(factory, provider, LatestMigration);

        var template = ResourceVisualCatalogSeed.Templates.First();
        await using (var db = factory.Create(provider))
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.resource_visual_templates SET sort_order = {template.SortOrder + 100} WHERE id = {template.Id}");
        await AssertSeedRollbackBlockedAsync(factory, provider, "metadata was modified");
        await using (var db = factory.Create(provider))
        {
            Assert.Equal(template.SortOrder + 100, await db.Set<ResourceVisualTemplate>().Where(t => t.Id == template.Id).Select(t => t.SortOrder).SingleAsync());
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE core.resource_visual_templates SET sort_order = {template.SortOrder} WHERE id = {template.Id}");
        }
        await MigrateAsync(factory, provider, LatestMigration);

        await MigrateAsync(factory, provider, LegacyMigration);
        Assert.Equal(original, await LegacySnapshotAsync(factory, provider, sentinel));
        await using (var db = factory.Create(provider))
        {
            Assert.Equal(LegacyMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
            Assert.Equal(new[]
            {
                "20261021225838_AddResourceVisualCatalog", MapMigration, SeedMigration, LatestMigration,
            }, await db.Database.GetPendingMigrationsAsync());
        }

        await MigrateAsync(factory, provider, LatestMigration);
        Assert.Equal(original, await LegacySnapshotAsync(factory, provider, sentinel));
        await AssertCatalogAsync(factory, provider, sentinel.AdminRoleId);
    }

    private static async Task AssertSeedRollbackBlockedAsync(PostgresAppDbFactory factory, FakeTenantProvider provider, string reason)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(factory, provider, MapMigration));
        Assert.Contains("RESOURCE_VISUAL_SEED_DOWN_BLOCKED", error.MessageText);
        Assert.Contains(reason, error.MessageText);
        await using var db = factory.Create(provider);
        Assert.Contains(SeedMigration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(ResourceVisualCatalogSeed.Revisions.Count, await db.Set<ResourceVisualTemplateRevision>().CountAsync());
    }

    private static async Task MigrateAsync(PostgresAppDbFactory factory, FakeTenantProvider provider, string target)
    {
        await using var db = factory.Create(provider);
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    private static async Task AssertCatalogAsync(PostgresAppDbFactory factory, FakeTenantProvider provider, Guid adminRoleId)
    {
        await using var db = factory.Create(provider);
        Assert.Equal(ResourceVisualCatalogSeed.Tags.Count, await db.Set<ResourceVisualTag>().CountAsync());
        Assert.Equal(ResourceVisualCatalogSeed.Templates.Count, await db.Set<ResourceVisualTemplate>().CountAsync());
        Assert.Equal(ResourceVisualCatalogSeed.Revisions.Count, await db.Set<ResourceVisualTemplateRevision>().CountAsync());
        Assert.Equal(ResourceVisualCatalogSeed.TemplateTags.Count, await db.Set<ResourceVisualTemplateTag>().CountAsync());
        Assert.Equal(4, await db.Database.SqlQuery<int>($"""
            SELECT count(*)::integer AS "Value"
            FROM core.role_permissions rp JOIN core.permissions p ON p.id = rp.permission_id
            WHERE rp.role_id = {adminRoleId} AND p.key IN (
                'rentals.segments.read', 'rentals.segments.write',
                'rentals.map_settings.read', 'rentals.map_settings.write')
            """).SingleAsync());
    }

    private static async Task<string> LegacySnapshotAsync(PostgresAppDbFactory factory, FakeTenantProvider provider, Sentinel sentinel)
    {
        await using var db = factory.Create(provider);
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
                'asset', (SELECT to_jsonb(a) FROM assets.assets a WHERE a.id = {sentinel.AssetId}),
                'resource', (SELECT to_jsonb(r) - ARRAY['capacity_people','visual_template_revision_id']
                    FROM rentals.rental_assets r WHERE r.id = {sentinel.RentalId}),
                'layout', (SELECT to_jsonb(l) - ARRAY['sort_order','version']
                    FROM rentals.layouts l WHERE l.id = {sentinel.LayoutId}),
                'item', (SELECT to_jsonb(i) - ARRAY['lock_aspect_ratio','rotation']
                    FROM rentals.layout_items i WHERE i.id = {sentinel.ItemId})
            )::text AS "Value"
            """).SingleAsync();
    }

    private static async Task<Sentinel> SeedLegacyRowsAsync(AppDbContext db, FakeTenantProvider provider)
    {
        var tenant = new Tenant("Migration sentinel", Guid.NewGuid().ToString("N")[..20]);
        provider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Legacy unit");
        var family = new AssetFamily { Key = "migration-sentinel", Label = "Spaces", FieldSchemaJson = "{}" };
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Legacy resources" };
        var asset = new Asset
        {
            TenantId = tenant.Id, UnitId = unit.Id, CategoryId = category.Id, FamilyId = family.Id,
            Name = "Legacy court", Tag = "LEGACY", Status = AssetStatus.Active, IsRentable = true,
        };
        var rental = new RentalAsset
        {
            TenantId = tenant.Id, AssetId = asset.Id, Type = RentalAssetType.Location, TotalQuantity = 1,
            IsActive = true, RequiresDeposit = false, SchedulePolicy = SchedulePolicy.SlotGrid,
        };
        var layout = new RentalLayout
        {
            TenantId = tenant.Id, UnitId = unit.Id, Name = "Legacy map", IsActive = true,
            AspectRatio = 1.6, WidthPercent = 95,
        };
        var item = new RentalLayoutItem
        {
            TenantId = tenant.Id, LayoutId = layout.Id, RentalAssetId = rental.Id,
            XPercent = 12, YPercent = 18, WidthPercent = 25, HeightPercent = 30, ZIndex = 7,
        };
        var role = new Role(tenant.Id, "Admin", isSystemRole: true);
        db.AddRange(tenant, unit, family, category, asset, rental, layout, item, role);
        await db.SaveChangesAsync();
        return new(asset.Id, rental.Id, layout.Id, item.Id, role.Id);
    }

    private sealed record Sentinel(Guid AssetId, Guid RentalId, Guid LayoutId, Guid ItemId, Guid AdminRoleId);
}
