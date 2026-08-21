using Platform.Api.Services.Trial;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Trial;

public sealed class TrialGuardTests
{
    [Fact]
    public async Task EnsureWritableAsync_allows_non_trial_tenant()
    {
        var tenant = new Tenant("Paid Club", "11111111000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_allows_active_trial()
    {
        var tenant = new Tenant("Trial Club", "22222222000191");
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow);
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_blocks_read_only_trial()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var tenant = new Tenant("Expired Trial", "33333333000191");
        tenant.ConfigureAsTrial(utcNow.AddDays(-(TrialLimits.TrialDays + 1)));
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureWritableAsync(CancellationToken.None));

        Assert.Equal(
            "This trial has ended. The workspace is read-only until it is purged.",
            ex.Message);
    }

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_blocks_eleventh_asset_on_trial()
    {
        var tenant = CreateActiveTrial("Asset Limit Club", "44444444000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        SeedAssets(db, tenant.Id, count: 10);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureCanCreateAssetsAsync(1, CancellationToken.None));

        Assert.Equal("Trial tenants are limited to 10 assets.", ex.Message);
    }

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_allows_tenth_asset_on_trial()
    {
        var tenant = CreateActiveTrial("Asset Room Club", "55555555000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        SeedAssets(db, tenant.Id, count: 9);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureCanCreateAssetsAsync(1, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_blocks_twenty_first_non_admin_seat()
    {
        var tenant = CreateActiveTrial("Invite Limit Club", "66666666000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        SeedUsers(db, tenant.Id, count: 20, emailPrefix: "staff");
        await db.SaveChangesAsync();

        var sut = new TrialGuard(
            db,
            tenantProvider,
            new FakePlatformAdminChecker("admin@rolvix.test"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None));

        Assert.Equal("Trial tenants are limited to 20 users.", ex.Message);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_excludes_platform_admin_emails_from_limit()
    {
        var tenant = CreateActiveTrial("Admin Seat Club", "77777777000191");
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        SeedUsers(db, tenant.Id, count: 19, emailPrefix: "staff");
        db.Users.Add(new User(
            tenant.Id,
            Guid.NewGuid().ToString(),
            "Platform Admin",
            "admin@rolvix.test"));
        await db.SaveChangesAsync();

        var sut = new TrialGuard(
            db,
            tenantProvider,
            new FakePlatformAdminChecker("admin@rolvix.test"));

        await sut.EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_is_noop_when_additional_count_is_below_one()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var tenant = new Tenant("Read-only Extra", "88888888000191");
        tenant.ConfigureAsTrial(utcNow.AddDays(-(TrialLimits.TrialDays + 1)));
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        SeedAssets(db, tenant.Id, count: 10);
        await db.SaveChangesAsync();

        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureCanCreateAssetsAsync(0, CancellationToken.None);
        await sut.EnsureCanCreateAssetsAsync(-3, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_is_noop_when_tenant_id_is_null()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_is_noop_when_tenant_is_missing()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var sut = new TrialGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureCanInviteUserAsync(Guid.NewGuid(), CancellationToken.None);
    }

    private static Tenant CreateActiveTrial(string legalName, string taxId)
    {
        var tenant = new Tenant(legalName, taxId);
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow);
        return tenant;
    }

    private static void SeedAssets(AppDbContext db, Guid tenantId, int count)
    {
        var unitId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var familyId = Guid.NewGuid();

        for (var i = 0; i < count; i++)
        {
            db.Assets.Add(new Asset
            {
                TenantId = tenantId,
                UnitId = unitId,
                CategoryId = categoryId,
                FamilyId = familyId,
                Name = $"Court {i}",
                Tag = $"TAG-{i}",
                Status = AssetStatus.Active,
            });
        }
    }

    private static void SeedUsers(AppDbContext db, Guid tenantId, int count, string emailPrefix)
    {
        for (var i = 0; i < count; i++)
        {
            db.Users.Add(new User(
                tenantId,
                Guid.NewGuid().ToString(),
                $"Member {i}",
                $"{emailPrefix}{i}@club.test"));
        }
    }
}
