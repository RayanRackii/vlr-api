using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Modules.ResourceVisuals;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.ResourceVisuals;

namespace Platform.Api.Tests.Assets;

public sealed class AssetVisualAssignmentPostgresTests(PostgresContainerFixture postgres) : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Registry_create_persists_published_assignment_and_horizon_atomically_and_invalid_choice_leaves_no_orphan()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        Guid assetId, revisionId;
        await using (var db = factory.Create(provider))
        {
            var seed = await SeedAsync(db, provider);
            var service = new AssetService(db, provider, new FakeTrialGuard(), new RentalConfigurationSync(db));
            var request = new CreateRentableRequest { Name = "Resource", Tag = "R", UnitId = seed.UnitId, CategoryId = seed.CategoryId, FamilyId = seed.FamilyId, VisualTemplateRevisionId = seed.RevisionId, CapacityPeople = 6, MaxAdvanceBookingDays = 19 };
            var created = await service.CreateRentableAsync(request, CancellationToken.None);
            (assetId, revisionId) = (created.Id, seed.RevisionId);
            var exception = await Assert.ThrowsAsync<ResourceVisualException>(() => service.CreateRentableAsync(request with { Tag = "INVALID", VisualTemplateRevisionId = Guid.NewGuid() }, CancellationToken.None));
            Assert.Equal("VISUAL_TEMPLATE_INVALID", exception.Code);
        }
        await using (var verify = factory.Create(provider))
        {
            var resource = await verify.Assets.Include(a => a.RentalConfiguration).SingleAsync(a => a.Id == assetId && a.TenantId == provider.TenantId);
            Assert.Equal(revisionId, resource.RentalConfiguration!.VisualTemplateRevisionId);
            Assert.Equal(6, resource.RentalConfiguration.CapacityPeople);
            Assert.Equal(19, resource.RentalConfiguration.MaxAdvanceBookingDays);
            Assert.False(await verify.Assets.AnyAsync(a => a.Tag == "INVALID" && a.TenantId == provider.TenantId));
            Assert.Equal(1, await verify.Assets.CountAsync(a => a.TenantId == provider.TenantId));
            Assert.Equal(1, await verify.RentalAssets.CountAsync(a => a.TenantId == provider.TenantId));
        }
    }

    [DockerFact]
    public async Task Composite_layout_reference_cannot_link_foreign_tenant_resource_and_visual_FK_rejects_missing_revision()
    {
        var factory = postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture unavailable.");
        var provider = new FakeTenantProvider();
        Guid owner, rentalId, layoutId;
        await using (var db = factory.Create(provider))
        {
            var seed = await SeedAsync(db, provider);
            owner = provider.TenantId!.Value;
            var asset = await new AssetService(db, provider, new FakeTrialGuard(), new RentalConfigurationSync(db)).CreateRentableAsync(new()
            { Name = "Resource", Tag = "R", UnitId = seed.UnitId, CategoryId = seed.CategoryId, FamilyId = seed.FamilyId }, CancellationToken.None);
            rentalId = asset.RentalConfig!.RentalAssetId;
            var other = new Tenant("Other", Guid.NewGuid().ToString("N")[..20], subdomain: "visual-other-" + Guid.NewGuid().ToString("N"));
            var layout = new RentalLayout { TenantId = other.Id, Name = "Other map", IsActive = true, AspectRatio = 1.6, WidthPercent = 100 };
            db.AddRange(other, layout); await db.SaveChangesAsync(); layoutId = layout.Id;
            provider.TenantId = other.Id;
        }
        await using (var invalid = factory.Create(provider))
        {
            invalid.RentalLayoutItems.Add(new() { TenantId = provider.TenantId!.Value, LayoutId = layoutId, RentalAssetId = rentalId, XPercent = 0, YPercent = 0, WidthPercent = 10, HeightPercent = 10, ZIndex = 0 });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        provider.TenantId = owner;
        await using (var invalid = factory.Create(provider))
        {
            var rental = await invalid.RentalAssets.SingleAsync(r => r.Id == rentalId && r.TenantId == owner);
            rental.VisualTemplateRevisionId = Guid.NewGuid();
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
    }

    private static async Task<Seed> SeedAsync(Platform.Core.Infrastructure.Persistence.AppDbContext db, FakeTenantProvider provider)
    {
        var tenant = new Tenant("Visual registry test", Guid.NewGuid().ToString("N")[..20], subdomain: "visual-" + Guid.NewGuid().ToString("N"));
        provider.TenantId = tenant.Id;
        var unit = new Unit(tenant.Id, "Site");
        var family = new AssetFamily { Key = "visual-" + Guid.NewGuid().ToString("N")[..20], Label = "Spaces", FieldSchemaJson = "{\"fields\":[]}" };
        var category = new AssetCategory { TenantId = tenant.Id, Name = "Resources" };
        var template = new ResourceVisualTemplate { Slug = "registry-" + Guid.NewGuid().ToString("N"), NameI18n = new() { ["en"] = "Resource" } };
        var revision = new ResourceVisualTemplateRevision { TemplateId = template.Id, RevisionNo = 1, FloorSvg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 100\"><rect width=\"100\" height=\"100\" fill=\"#AABBCC\"/></svg>" };
        ResourceVisualArt.PreparePublication(revision, DateTimeOffset.UtcNow);
        db.AddRange(tenant, unit, family, category, new TenantAssetFamily(tenant.Id, family.Id), template, revision);
        await db.SaveChangesAsync();
        return new(unit.Id, category.Id, family.Id, revision.Id);
    }

    private sealed record Seed(Guid UnitId, Guid CategoryId, Guid FamilyId, Guid RevisionId);
}
