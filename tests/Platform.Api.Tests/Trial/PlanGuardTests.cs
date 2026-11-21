using Platform.Api.Services.Billing;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Trial;

public sealed class PlanGuardTests
{
    private const string ReadOnlyTrialMessage =
        "This trial has ended. The workspace is read-only until it is purged.";

    // --- read-only mode ---

    [Fact]
    public async Task EnsureWritableAsync_allows_tenant_without_subscription()
    {
        var tenant = new Tenant("Paid Club", "11111111000191");
        await using var harness = await Harness.CreateAsync(tenant);

        await harness.Guard().EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_allows_active_trial()
    {
        var tenant = CreateActiveTrial("Trial Club", "22222222000191");
        await using var harness = await Harness.CreateAsync(tenant);

        await harness.Guard().EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_blocks_read_only_trial()
    {
        var tenant = new Tenant("Expired Trial", "33333333000191");
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-(TrialLimits.TrialDays + 1)));
        await using var harness = await Harness.CreateAsync(tenant);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Guard().EnsureWritableAsync(CancellationToken.None));

        Assert.Equal(ReadOnlyTrialMessage, ex.Message);
    }

    [Fact]
    public async Task EnsureWritableAsync_ignores_the_trial_clock_when_the_subscription_is_active_or_past_due()
    {
        foreach (var status in new[] { SubscriptionStatuses.Active, SubscriptionStatuses.PastDue })
        {
            var tenant = new Tenant("Converted " + status, Guid.NewGuid().ToString("N")[..14]);
            tenant.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-(TrialLimits.TrialDays + 1)));
            await using var harness = await Harness.CreateAsync(tenant);
            var plan = harness.AddPlan(BillingTestData.CatalogRows[1]);
            BillingTestData.Subscribe(harness.Db, tenant, plan, status);
            await harness.Db.SaveChangesAsync();

            await harness.Guard().EnsureWritableAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Subscription_read_only_status_blocks_every_write_check_of_a_provider_billed_tenant()
    {
        var tenant = new Tenant("Overdue Club", "34343434000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddPlan(BillingTestData.CatalogRows[1]);
        BillingTestData.Subscribe(harness.Db, tenant, plan, SubscriptionStatuses.ReadOnly);
        await harness.Db.SaveChangesAsync();
        var sut = harness.Guard();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.EnsureWritableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureCanCreateAssetsAsync(1, 1, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Subscription_read_only_status_never_blocks_a_manually_billed_tenant()
    {
        var tenant = new Tenant("Manual Club", "35353535000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddPlan(BillingTestData.CatalogRows[2]);
        BillingTestData.Subscribe(
            harness.Db, tenant, plan, SubscriptionStatuses.ReadOnly, BillingModes.Manual, limitsUnlimited: true);
        await harness.Db.SaveChangesAsync();

        await harness.Guard().EnsureWritableAsync(CancellationToken.None);
        await harness.Guard().EnsureCanCreateAssetsAsync(1, 1, CancellationToken.None);
    }

    // --- limits ---

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_blocks_past_the_assets_limit_with_the_plan_limit_error()
    {
        var tenant = CreateActiveTrial("Asset Limit Club", "44444444000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("manutencao", "essencial", assets: 3);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedAssets(harness.Db, tenant.Id, count: 3);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard().EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None));

        Assert.Equal(PlanLimitKeys.Assets, ex.Limit);
        Assert.Equal(3, ex.Current);
        Assert.Equal(3, ex.Max);
        Assert.Equal("essencial", ex.PlanKey);
        Assert.Equal("plan.limit_reached", PlanLimitReachedException.Code);
    }

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_allows_the_asset_that_reaches_the_limit_and_blocks_a_bulk_that_passes_it()
    {
        var tenant = CreateActiveTrial("Asset Room Club", "55555555000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("manutencao", "essencial", assets: 3);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedAssets(harness.Db, tenant.Id, count: 2);
        await harness.Db.SaveChangesAsync();
        var sut = harness.Guard();

        await sut.EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => sut.EnsureCanCreateAssetsAsync(2, 0, CancellationToken.None));
        Assert.Equal(2, ex.Current);
    }

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_applies_rentable_resources_only_to_rentable_creates()
    {
        var tenant = CreateActiveTrial("Courts Club", "56565656000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("reservas", "essencial", rentable: 2);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedAssets(harness.Db, tenant.Id, count: 2, isRentable: true);
        SeedAssets(harness.Db, tenant.Id, count: 5, isRentable: false, tagPrefix: "EQ");
        await harness.Db.SaveChangesAsync();
        var sut = harness.Guard();

        await sut.EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => sut.EnsureCanCreateAssetsAsync(1, 1, CancellationToken.None));

        Assert.Equal(PlanLimitKeys.RentableResources, ex.Limit);
        Assert.Equal(2, ex.Current);
        Assert.Equal(2, ex.Max);
    }

    [Fact]
    public async Task Trial_uses_the_profissional_limits_of_its_package()
    {
        var tenant = CreateActiveTrial("Pro Trial Club", "57575757000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddPlan(BillingTestData.CatalogRows[1]);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedAssets(harness.Db, tenant.Id, count: 8, isRentable: true);
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard().EnsureCanCreateAssetsAsync(1, 1, CancellationToken.None));

        Assert.Equal(PlanLimitKeys.RentableResources, ex.Limit);
        Assert.Equal(8, ex.Max);
        Assert.Equal("profissional", ex.PlanKey);
    }

    [Fact]
    public async Task Tenant_without_subscription_or_with_unlimited_limits_is_never_limited()
    {
        var bare = CreateActiveTrial("Bare Club", "58585858000191");
        await using var harness = await Harness.CreateAsync(bare);
        SeedAssets(harness.Db, bare.Id, count: 40, isRentable: true);
        await harness.Db.SaveChangesAsync();
        await harness.Guard().EnsureCanCreateAssetsAsync(100, 100, CancellationToken.None);
        await harness.Guard().EnsureCanCreateUnitAsync(bare.Id, CancellationToken.None);

        var unlimited = new Tenant("Backfilled Club", "59595959000191");
        harness.Db.Tenants.Add(unlimited);
        var plan = harness.AddCustomPlan("reservas", "completo", assets: 1, rentable: 1, users: 1, units: 1);
        BillingTestData.Subscribe(
            harness.Db, unlimited, plan, SubscriptionStatuses.Active, BillingModes.Manual, limitsUnlimited: true);
        harness.Db.Units.Add(new Unit(unlimited.Id, "HQ"));
        SeedAssets(harness.Db, unlimited.Id, count: 5, isRentable: true, tagPrefix: "U");
        await harness.Db.SaveChangesAsync();
        harness.TenantProvider.TenantId = unlimited.Id;

        await harness.Guard().EnsureCanCreateAssetsAsync(10, 10, CancellationToken.None);
        await harness.Guard().EnsureCanCreateUnitAsync(unlimited.Id, CancellationToken.None);
        await harness.Guard().EnsureCanInviteUserAsync(unlimited.Id, CancellationToken.None);
    }

    [Fact]
    public async Task An_ended_subscription_is_ignored()
    {
        var tenant = CreateActiveTrial("Ended Sub Club", "60606060000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("manutencao", "essencial", assets: 1);
        var subscription = BillingTestData.Subscribe(harness.Db, tenant, plan);
        subscription.EndedAt = DateTimeOffset.UtcNow.AddDays(-1);
        SeedAssets(harness.Db, tenant.Id, count: 3);
        await harness.Db.SaveChangesAsync();

        await harness.Guard().EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_blocks_the_seat_past_the_users_limit()
    {
        var tenant = CreateActiveTrial("Invite Limit Club", "66666666000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("reservas", "essencial", users: 3);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedUsers(harness.Db, tenant.Id, count: 3, emailPrefix: "staff");
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard("admin@rolvix.test").EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None));

        Assert.Equal(PlanLimitKeys.Users, ex.Limit);
        Assert.Equal(3, ex.Current);
        Assert.Equal(3, ex.Max);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_counts_pending_invites_and_excludes_platform_admin_emails()
    {
        var tenant = CreateActiveTrial("Admin Seat Club", "77777777000191");
        await using var harness = await Harness.CreateAsync(tenant);
        var plan = harness.AddCustomPlan("reservas", "essencial", users: 3);
        BillingTestData.Subscribe(harness.Db, tenant, plan);
        SeedUsers(harness.Db, tenant.Id, count: 2, emailPrefix: "staff");
        harness.Db.Users.Add(new User(
            tenant.Id,
            Guid.NewGuid().ToString(),
            "Platform Admin",
            "admin@rolvix.test"));
        await harness.Db.SaveChangesAsync();

        // 2 members + the excluded platform admin: one seat left.
        await harness.Guard("admin@rolvix.test").EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None);

        harness.Db.UserInvites.Add(new UserInvite(
            tenant.Id, "pending@club.test", "Pending", "Admin", Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow.AddDays(3)));
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard("admin@rolvix.test").EnsureCanInviteUserAsync(tenant.Id, CancellationToken.None));
    }

    [Fact]
    public async Task EnsureCanCreateUnitAsync_blocks_past_the_units_limit_per_tenant()
    {
        var first = CreateActiveTrial("Unit Club", "61616161000191");
        var second = CreateActiveTrial("Other Unit Club", "62626262000191");
        await using var harness = await Harness.CreateAsync(first);
        harness.Db.Tenants.Add(second);
        var plan = harness.AddCustomPlan("reservas", "essencial", units: 1);
        BillingTestData.Subscribe(harness.Db, first, plan);
        BillingTestData.Subscribe(harness.Db, second, plan);
        harness.Db.Units.Add(new Unit(first.Id, "HQ"));
        await harness.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard().EnsureCanCreateUnitAsync(first.Id, CancellationToken.None));
        Assert.Equal(PlanLimitKeys.Units, ex.Limit);

        // The other tenant has no unit yet: first tenant's usage does not count against it.
        await harness.Guard().EnsureCanCreateUnitAsync(second.Id, CancellationToken.None);
    }

    [Fact]
    public async Task One_tenant_reaching_its_limit_does_not_limit_another_tenant()
    {
        var full = CreateActiveTrial("Full Club", "63636363000191");
        var roomy = CreateActiveTrial("Roomy Club", "64646464000191");
        await using var harness = await Harness.CreateAsync(full);
        harness.Db.Tenants.Add(roomy);
        var small = harness.AddCustomPlan("manutencao", "essencial", assets: 1);
        var large = harness.AddCustomPlan("manutencao", "completo", assets: 100);
        BillingTestData.Subscribe(harness.Db, full, small);
        BillingTestData.Subscribe(harness.Db, roomy, large);
        SeedAssets(harness.Db, full.Id, count: 1, tagPrefix: "F");
        await harness.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<PlanLimitReachedException>(
            () => harness.Guard().EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None));

        harness.TenantProvider.TenantId = roomy.Id;
        await harness.Guard().EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None);
    }

    // --- IPlanLimits ---

    [Fact]
    public async Task PlanLimits_returns_the_catalog_limits_of_every_package_and_plan()
    {
        foreach (var row in BillingTestData.CatalogRows)
        {
            var tenant = new Tenant($"{row.Package} {row.Plan}", Guid.NewGuid().ToString("N")[..14]);
            await using var harness = await Harness.CreateAsync(tenant);
            var plan = harness.AddPlan(row);
            BillingTestData.Subscribe(harness.Db, tenant, plan);
            await harness.Db.SaveChangesAsync();

            var limits = await new PlanLimitsService(harness.Db).GetAsync(tenant.Id, CancellationToken.None);

            Assert.True(limits.HasSubscription);
            Assert.False(limits.IsUnlimited);
            Assert.Equal(row.Package, limits.PackageKey);
            Assert.Equal(row.Plan, limits.PlanKey);
            Assert.Equal(row.Assets, limits.Limits.Assets);
            Assert.Equal(row.Rentable, limits.Limits.RentableResources);
            Assert.Equal(row.Users, limits.Limits.Users);
            Assert.Equal(row.Units, limits.Limits.Units);
            Assert.Equal(row.Whatsapp, limits.Limits.WhatsappMessagesMonth);
        }
    }

    [Fact]
    public async Task PlanLimits_is_unlimited_without_a_subscription()
    {
        var tenant = new Tenant("No Sub Club", "65656565000191");
        await using var harness = await Harness.CreateAsync(tenant);

        var limits = await new PlanLimitsService(harness.Db).GetAsync(tenant.Id, CancellationToken.None);

        Assert.False(limits.HasSubscription);
        Assert.True(limits.IsUnlimited);
        Assert.Equal(PlanLimitValues.Unlimited, limits.Limits);
    }

    // --- no-ops ---

    [Fact]
    public async Task EnsureCanCreateAssetsAsync_is_noop_when_additional_count_is_below_one()
    {
        var tenant = new Tenant("Read-only Extra", "88888888000191");
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-(TrialLimits.TrialDays + 1)));
        await using var harness = await Harness.CreateAsync(tenant);
        SeedAssets(harness.Db, tenant.Id, count: 10);
        await harness.Db.SaveChangesAsync();

        await harness.Guard().EnsureCanCreateAssetsAsync(0, 0, CancellationToken.None);
        await harness.Guard().EnsureCanCreateAssetsAsync(-3, 0, CancellationToken.None);
    }

    [Fact]
    public async Task EnsureWritableAsync_is_noop_when_tenant_id_is_null()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = null };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var sut = new PlanGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureWritableAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureCanInviteUserAsync_is_noop_when_tenant_is_missing()
    {
        var tenantProvider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var sut = new PlanGuard(db, tenantProvider, new FakePlatformAdminChecker());

        await sut.EnsureCanInviteUserAsync(Guid.NewGuid(), CancellationToken.None);
    }

    // --- trial dates ---

    [Fact]
    public void ConfigureAsTrial_uses_30_days_of_use_and_90_days_until_purge()
    {
        var createdAt = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero);
        var tenant = new Tenant("Dates Club", "70707070000191");

        tenant.ConfigureAsTrial(createdAt);

        Assert.Equal(30, TrialLimits.TrialDays);
        Assert.Equal(90, TrialLimits.PurgeDays);
        Assert.Equal(createdAt.AddDays(30), tenant.TrialEndsAt);
        Assert.Equal(createdAt.AddDays(90), tenant.TrialPurgeAt);
        Assert.False(tenant.IsTrialReadOnly(createdAt.AddDays(29)));
        Assert.True(tenant.IsTrialReadOnly(createdAt.AddDays(31)));
        Assert.False(tenant.IsTrialReadOnly(createdAt.AddDays(91)));
    }

    [Fact]
    public void ConvertFromTrial_clears_the_flag_and_both_dates()
    {
        var tenant = CreateActiveTrial("Convert Club", "71717171000191");

        tenant.ConvertFromTrial();

        Assert.False(tenant.IsTrial);
        Assert.Null(tenant.TrialEndsAt);
        Assert.Null(tenant.TrialPurgeAt);
        Assert.False(tenant.IsTrialReadOnly(DateTimeOffset.UtcNow.AddDays(500)));
    }

    private static Tenant CreateActiveTrial(string legalName, string taxId)
    {
        var tenant = new Tenant(legalName, taxId);
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow);
        return tenant;
    }

    private static void SeedAssets(
        AppDbContext db,
        Guid tenantId,
        int count,
        bool isRentable = false,
        string tagPrefix = "TAG")
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
                Tag = $"{tagPrefix}-{i}",
                Status = AssetStatus.Active,
                IsRentable = isRentable,
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

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(AppDbContext db, FakeTenantProvider tenantProvider)
        {
            Db = db;
            TenantProvider = tenantProvider;
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public static async Task<Harness> CreateAsync(Tenant tenant)
        {
            var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
            var db = InMemoryAppDb.Create(tenantProvider);
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            return new Harness(db, tenantProvider);
        }

        public PlanGuard Guard(params string[] platformAdminEmails) =>
            new(Db, TenantProvider, new FakePlatformAdminChecker(platformAdminEmails));

        public PlanCatalog AddPlan(BillingTestData.CatalogRow row)
        {
            var plan = BillingTestData.Plan(
                row.Package,
                row.Plan,
                BillingTestData.LimitsJson(row.Assets, row.Rentable, row.Users, row.Units, row.Whatsapp),
                row.Monthly,
                row.Annual,
                row.Modules);
            Db.PlanCatalogs.Add(plan);
            Db.SaveChanges();
            return plan;
        }

        public PlanCatalog AddCustomPlan(
            string package,
            string plan,
            int? assets = null,
            int? rentable = null,
            int? users = null,
            int? units = null)
        {
            var entity = BillingTestData.Plan(
                package,
                plan,
                BillingTestData.LimitsJson(assets, rentable, users, units, 0));
            Db.PlanCatalogs.Add(entity);
            Db.SaveChanges();
            return entity;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
