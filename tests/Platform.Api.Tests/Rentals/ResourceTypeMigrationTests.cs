using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Rentals;

public sealed class ResourceTypeMigrationTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Seeds_quadra_and_cacamba_and_links_matching_categories()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        await using var db = factory.Create(provider);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261112171214_AddRentalAssetDirectRental";
        const string target = "20261112180443_AddResourceTypes";
        await migrator.MigrateAsync(before);

        var tenant = new Tenant("Tipos", Guid.NewGuid().ToString("N")[..14], subdomain: $"tipos-{Guid.NewGuid():N}"[..20]);
        provider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Matriz");
        var family = new AssetFamily { Key = $"fam-{Guid.NewGuid():N}"[..32], Label = "Spaces", FieldSchemaJson = """{"fields":[]}""" };
        var quadraCategory = new AssetCategory { TenantId = tenant.Id, Name = "Quadra" };
        var salaCategory = new AssetCategory { TenantId = tenant.Id, Name = "Sala" };
        var quadraAsset = new Asset
        {
            TenantId = tenant.Id, UnitId = unit.Id, CategoryId = quadraCategory.Id, FamilyId = family.Id,
            Name = "Quadra 1", Tag = "Q1", Status = Platform.Core.Domain.Enums.AssetStatus.Active,
        };
        var salaAsset = new Asset
        {
            TenantId = tenant.Id, UnitId = unit.Id, CategoryId = salaCategory.Id, FamilyId = family.Id,
            Name = "Sala 1", Tag = "S1", Status = Platform.Core.Domain.Enums.AssetStatus.Active,
        };
        db.AddRange(tenant, unit, family, quadraCategory, salaCategory, quadraAsset, salaAsset);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO rentals.rental_assets
                (id, tenant_id, asset_id, type, total_quantity, is_active, requires_deposit, schedule_policy, created_at, direct_rental)
            VALUES
                ({Guid.NewGuid()}, {tenant.Id}, {quadraAsset.Id}, 'Location', 1, true, false, 'SlotGrid', now(), true),
                ({Guid.NewGuid()}, {tenant.Id}, {salaAsset.Id}, 'Location', 1, true, false, 'SlotGrid', now(), true)
            """);

        await migrator.MigrateAsync(target);

        var types = await db.ResourceTypes.AsNoTracking().Where(type => type.TenantId == tenant.Id).ToListAsync();
        Assert.Equal(["cacamba", "quadra"], types.Select(type => type.Key).Order().ToArray());
        var quadraType = types.Single(type => type.Key == "quadra");
        var linked = await db.RentalAssets.AsNoTracking().SingleAsync(asset => asset.AssetId == quadraAsset.Id);
        var unlinked = await db.RentalAssets.AsNoTracking().SingleAsync(asset => asset.AssetId == salaAsset.Id);
        Assert.Equal(quadraType.Id, linked.ResourceTypeId);
        Assert.Null(unlinked.ResourceTypeId);
    }
}
