using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authentication;
using Platform.Api.Jobs;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Modules.Billing.Controllers;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;
using Platform.Api.Services.Billing;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Billing;

public sealed class AdminSubscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 12, 0, 0, TimeSpan.Zero);

    // --- GET / PUT ---

    [Fact]
    public async Task Get_reports_no_subscription_for_a_tenant_without_one_and_404_for_an_unknown_tenant()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("No Sub");

        var dto = await h.Service.GetAsync(tenant.Id, CancellationToken.None);

        Assert.False(dto.HasSubscription);
        Assert.Equal(tenant.Id, dto.TenantId);
        Assert.Empty(dto.Events);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Get_returns_only_the_requested_tenant_subscription()
    {
        await using var h = await Harness.CreateAsync();
        var first = await h.AddTenantAsync("First");
        var second = await h.AddTenantAsync("Second");
        await h.Service.PutAsync(first.Id, new() { PlanCatalogId = h.Plan("reservas", "essencial").Id }, "admin", CancellationToken.None);
        await h.Service.PutAsync(second.Id, new() { PlanCatalogId = h.Plan("locacao", "completo").Id }, "admin", CancellationToken.None);

        var dto = await h.Service.GetAsync(first.Id, CancellationToken.None);

        Assert.Equal("reservas", dto.PackageKey);
        Assert.Equal("essencial", dto.PlanKey);
        Assert.Equal(new PlanLimitsDto(null, 3, 3, 1, 0), dto.PlanLimits);
        Assert.Equal(9900, dto.MonthlyEquivalentCents);
    }

    [Fact]
    public async Task Put_creates_a_manually_billed_active_subscription_with_a_created_event()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Pilot");

        var dto = await h.Service.PutAsync(
            tenant.Id, new() { PlanCatalogId = h.Plan("manutencao", "profissional").Id }, "admin-1", CancellationToken.None);

        Assert.True(dto.HasSubscription);
        Assert.Equal(SubscriptionStatuses.Active, dto.Status);
        Assert.Equal(BillingModes.Manual, dto.BillingMode);
        Assert.Equal(BillingCycles.Monthly, dto.BillingCycle);
        var created = Assert.Single(dto.Events);
        Assert.Equal(SubscriptionEventTypes.Created, created.Type);
    }

    [Fact]
    public async Task Put_without_a_plan_is_rejected_when_the_tenant_has_no_subscription()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("No plan");

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, new() { Status = SubscriptionStatuses.Active }, null, CancellationToken.None));
    }

    [Fact]
    public async Task Put_for_an_unknown_tenant_is_not_found()
    {
        await using var h = await Harness.CreateAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => h.Service.PutAsync(Guid.NewGuid(), new() { PlanCatalogId = h.Plan("reservas", "essencial").Id }, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("status", "paused")]
    [InlineData("cycle", "weekly")]
    [InlineData("mode", "stripe")]
    [InlineData("discount", "101")]
    [InlineData("discount", "-1")]
    [InlineData("coupon", "unknown")]
    public async Task Put_rejects_invalid_values(string field, string value)
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Validation");
        var planId = h.Plan("reservas", "essencial").Id;
        PutTenantSubscriptionRequestDto request = field switch
        {
            "status" => new() { PlanCatalogId = planId, Status = value },
            "cycle" => new() { PlanCatalogId = planId, BillingCycle = value },
            "mode" => new() { PlanCatalogId = planId, BillingMode = value },
            "discount" => new() { PlanCatalogId = planId, DiscountPercent = int.Parse(value) },
            _ => new() { PlanCatalogId = planId, Coupon = value },
        };

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, request, null, CancellationToken.None));
        Assert.Empty(await h.Db.TenantSubscriptions.ToListAsync());
    }

    [Fact]
    public async Task Put_rejects_unknown_and_inactive_plans()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Plans");
        var inactive = BillingTestData.Plan("reservas", "legacy", "{}", active: false);
        h.Db.PlanCatalogs.Add(inactive);
        await h.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, new() { PlanCatalogId = Guid.NewGuid() }, null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, new() { PlanCatalogId = inactive.Id }, null, CancellationToken.None));
    }

    [Fact]
    public async Task Put_fundador_coupon_applies_40_percent_for_12_months_and_cannot_be_combined()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Founder");
        var planId = h.Plan("reservas", "profissional").Id;

        var dto = await h.Service.PutAsync(
            tenant.Id, new() { PlanCatalogId = planId, Coupon = "Fundador" }, null, CancellationToken.None);

        Assert.Equal(40, dto.DiscountPercent);
        Assert.Equal(Now.AddMonths(12), dto.DiscountUntil);
        Assert.Equal(40, dto.EffectiveDiscountPercent);
        Assert.Equal(11340, dto.MonthlyEquivalentCents);
        await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, new() { Coupon = "fundador", DiscountPercent = 10 }, null, CancellationToken.None));

        var cleared = await h.Service.PutAsync(
            tenant.Id, new() { DiscountPercent = 0 }, null, CancellationToken.None);
        Assert.Equal(0, cleared.DiscountPercent);
        Assert.Null(cleared.DiscountUntil);
    }

    [Fact]
    public async Task Put_is_idempotent_and_records_one_event_per_real_change()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Idempotent");
        var planId = h.Plan("reservas", "essencial").Id;
        await h.Service.PutAsync(tenant.Id, new() { PlanCatalogId = planId }, null, CancellationToken.None);

        await h.Service.PutAsync(tenant.Id, new() { PlanCatalogId = planId, Status = SubscriptionStatuses.Active }, null, CancellationToken.None);
        var changed = await h.Service.PutAsync(
            tenant.Id,
            new() { PlanCatalogId = h.Plan("reservas", "completo").Id, BillingCycle = BillingCycles.Annual },
            null,
            CancellationToken.None);

        Assert.Equal(2, changed.Events.Count);
        Assert.Contains(changed.Events, e => e.Type == SubscriptionEventTypes.PlanChanged);
        Assert.Equal("completo", changed.PlanKey);
        Assert.Equal(BillingCycles.Annual, changed.BillingCycle);
        Assert.Equal(24917, changed.MonthlyEquivalentCents);
    }

    [Fact]
    public async Task Put_read_only_requires_a_provider_billed_row_and_reactivation_is_recorded()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = await h.AddTenantAsync("Block");
        var planId = h.Plan("reservas", "essencial").Id;
        await h.Service.PutAsync(tenant.Id, new() { PlanCatalogId = planId }, null, CancellationToken.None);

        var refused = await Assert.ThrowsAsync<ArgumentException>(
            () => h.Service.PutAsync(tenant.Id, new() { Status = SubscriptionStatuses.ReadOnly }, null, CancellationToken.None));
        Assert.Contains("BillingMode provider", refused.Message);

        var blocked = await h.Service.PutAsync(
            tenant.Id,
            new() { Status = SubscriptionStatuses.ReadOnly, BillingMode = BillingModes.Provider },
            null,
            CancellationToken.None);
        Assert.Contains(blocked.Events, e => e.Type == SubscriptionEventTypes.ReadOnly);

        var back = await h.Service.PutAsync(
            tenant.Id, new() { Status = SubscriptionStatuses.Active }, null, CancellationToken.None);
        Assert.Contains(back.Events, e => e.Type == SubscriptionEventTypes.Reactivated);
        Assert.Null(back.PastDueSince);
    }

    [Fact]
    public async Task Put_active_converts_a_trial_tenant_so_it_is_not_read_only_after_the_trial_end_nor_purged()
    {
        await using var h = await Harness.CreateAsync();
        var tenant = new Tenant("Trial Converter", Guid.NewGuid().ToString("N")[..14]);
        // Created 100 days ago: the trial ended and the purge date passed.
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-100));
        h.Db.Tenants.Add(tenant);
        var plan = h.Plan("reservas", "profissional");
        BillingTestData.Subscribe(h.Db, tenant, plan);
        await h.Db.SaveChangesAsync();
        h.TenantProvider.TenantId = tenant.Id;
        var guard = new PlanGuard(h.Db, h.TenantProvider, new FakePlatformAdminChecker());

        // Past the purge date the trial would be purged by the lifecycle job.
        var deleted = new List<Guid>();
        var job = new TrialLifecycleJob(h.Db, new RecordingAdminTenantService(deleted), NullLogger<TrialLifecycleJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(new[] { tenant.Id }, deleted);
        deleted.Clear();

        await h.Service.PutAsync(
            tenant.Id, new() { Status = SubscriptionStatuses.Active }, "admin", CancellationToken.None);

        h.Db.ChangeTracker.Clear();
        var reloaded = await h.Db.Tenants.SingleAsync(t => t.Id == tenant.Id);
        Assert.False(reloaded.IsTrial);
        Assert.Null(reloaded.TrialEndsAt);
        Assert.Null(reloaded.TrialPurgeAt);
        await guard.EnsureWritableAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Empty(deleted);
    }

    // --- TrialLifecycleJob ---

    [Theory]
    [InlineData(SubscriptionStatuses.Active, false)]
    [InlineData(SubscriptionStatuses.PastDue, false)]
    [InlineData(SubscriptionStatuses.Trialing, true)]
    [InlineData(SubscriptionStatuses.ReadOnly, true)]
    [InlineData(SubscriptionStatuses.Canceled, true)]
    public async Task TrialLifecycleJob_purges_a_due_trial_unless_its_subscription_is_active_or_past_due(
        string status,
        bool purged)
    {
        await using var h = await Harness.CreateAsync();
        var tenant = new Tenant("Due Trial", Guid.NewGuid().ToString("N")[..14]);
        tenant.ConfigureAsTrial(DateTimeOffset.UtcNow.AddDays(-(TrialLimits.PurgeDays + 1)));
        h.Db.Tenants.Add(tenant);
        BillingTestData.Subscribe(h.Db, tenant, h.Plan("reservas", "profissional"), status);
        await h.Db.SaveChangesAsync();
        var deleted = new List<Guid>();
        var job = new TrialLifecycleJob(h.Db, new RecordingAdminTenantService(deleted), NullLogger<TrialLifecycleJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);

        Assert.Equal(purged, deleted.Contains(tenant.Id));
    }

    // --- summary ---

    [Fact]
    public async Task Summary_sums_the_monthly_equivalent_after_discount_of_active_and_past_due_subscriptions()
    {
        await using var h = await Harness.CreateAsync();

        async Task Add(string package, string plan, string status, string cycle = BillingCycles.Monthly, int discount = 0, DateTimeOffset? until = null)
        {
            var tenant = await h.AddTenantAsync($"{package}-{plan}-{status}-{Guid.NewGuid():N}");
            var sub = BillingTestData.Subscribe(h.Db, tenant, h.Plan(package, plan), status);
            sub.BillingCycle = cycle;
            sub.DiscountPercent = discount;
            sub.DiscountUntil = until;
            await h.Db.SaveChangesAsync();
        }

        await Add("reservas", "profissional", SubscriptionStatuses.Active);                                        // 18900
        await Add("reservas", "profissional", SubscriptionStatuses.Active, BillingCycles.Annual, 40);              // 189000/12*0.6 = 9450
        await Add("manutencao", "essencial", SubscriptionStatuses.PastDue, discount: 100);                         // 0
        await Add("locacao", "essencial", SubscriptionStatuses.Active, discount: 50, until: Now.AddDays(-1));     // discount expired: 9900
        await Add("locacao", "completo", SubscriptionStatuses.Trialing);
        await Add("locacao", "completo", SubscriptionStatuses.ReadOnly);
        await Add("locacao", "completo", SubscriptionStatuses.Canceled);

        var summary = await new AdminBillingSummaryService(h.Db, h.Time).GetAsync(CancellationToken.None);

        Assert.Equal(38250, summary.MrrCents);
        Assert.Equal("BRL", summary.Currency);
        Assert.Equal(1, summary.TrialingCount);
        Assert.Equal(3, summary.ActiveCount);
        Assert.Equal(1, summary.PastDueCount);
        Assert.Equal(1, summary.ReadOnlyCount);
        Assert.Equal(28350, summary.ByPackage.Single(p => p.PackageKey == "reservas").MrrCents);
        Assert.Equal(0, summary.ByPackage.Single(p => p.PackageKey == "manutencao").MrrCents);
        var locacao = summary.ByPackage.Single(p => p.PackageKey == "locacao");
        Assert.Equal(9900, locacao.MrrCents);
        Assert.Equal(1, locacao.TrialingCount);
        Assert.Equal(1, locacao.ReadOnlyCount);
        Assert.Equal(3, summary.ByPackage.Count);
    }

    [Fact]
    public async Task Summary_is_empty_without_subscriptions()
    {
        await using var h = await Harness.CreateAsync();

        var summary = await new AdminBillingSummaryService(h.Db, h.Time).GetAsync(CancellationToken.None);

        Assert.Equal(0, summary.MrrCents);
        Assert.Equal(3, summary.ByPackage.Count);
    }

    // --- public plans ---

    [Fact]
    public async Task Public_plans_list_only_active_rows_in_sort_order_with_modules_and_limits()
    {
        await using var h = await Harness.CreateAsync();
        var hidden = BillingTestData.Plan("reservas", "hidden", "{}", active: false);
        hidden.SortOrder = 0;
        h.Db.PlanCatalogs.Add(hidden);
        await h.Db.SaveChangesAsync();

        var plans = await new PlanCatalogService(h.Db).ListActiveAsync(CancellationToken.None);

        Assert.Equal(9, plans.Count);
        Assert.DoesNotContain(plans, p => p.PlanKey == "hidden");
        Assert.Equal(plans.OrderBy(p => p.SortOrder).Select(p => p.Id), plans.Select(p => p.Id));
        var completo = plans.Single(p => p.PackageKey == "manutencao" && p.PlanKey == "completo");
        Assert.Equal(["inventory", "pmoc", "os", "resource-planning"], completo.Modules);
        Assert.Equal(new PlanLimitsDto(3000, null, 15, 1, 2000), completo.Limits);
        Assert.Equal(29900, completo.MonthlyPriceCents);
        Assert.Equal(299000, completo.AnnualPriceCents);
    }

    // --- authorization wiring ---

    [Fact]
    public void Admin_billing_controllers_are_platform_admin_only_and_the_public_plans_read_is_anonymous()
    {
        foreach (var type in new[] { typeof(AdminTenantSubscriptionsController), typeof(AdminBillingController) })
        {
            var authorize = Assert.Single(type.GetCustomAttributes<AuthorizeAttribute>());
            Assert.Equal(SupabaseAuthenticationExtensions.PlatformAdminPolicy, authorize.Policy);
            Assert.Empty(type.GetCustomAttributes<AllowAnonymousAttribute>());
            Assert.All(
                type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
                method => Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>()));
        }

        var list = typeof(PublicPlansController).GetMethod(nameof(PublicPlansController.List))!;
        Assert.NotEmpty(list.GetCustomAttributes<AllowAnonymousAttribute>());
        Assert.Empty(typeof(PublicPlansController).GetCustomAttributes<AuthorizeAttribute>());
    }

    private sealed class RecordingAdminTenantService(List<Guid> deleted) : IAdminTenantService
    {
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            deleted.Add(id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TenantAdminResponseDto>> ListAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantAdminResponseDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantAdminResponseDto> CreateAsync(CreateTenantRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantAdminResponseDto> UpdateAsync(Guid id, UpdateTenantRequestDto request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<EnterTenantEnvironmentResponseDto> EnterEnvironmentAsync(
            Guid tenantId, ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ExitEnvironmentAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<PlanCatalog> _plans;

        private Harness(AppDbContext db, FakeTenantProvider provider, List<PlanCatalog> plans, TestTimeProvider time)
        {
            Db = db;
            TenantProvider = provider;
            _plans = plans;
            Time = time;
            Service = new AdminSubscriptionService(db, time);
        }

        public AppDbContext Db { get; }

        public FakeTenantProvider TenantProvider { get; }

        public TestTimeProvider Time { get; }

        public AdminSubscriptionService Service { get; }

        public static async Task<Harness> CreateAsync()
        {
            var provider = new FakeTenantProvider { TenantId = null };
            var db = InMemoryAppDb.Create(provider);
            var plans = BillingTestData.AddCatalog(db).ToList();
            await db.SaveChangesAsync();
            return new Harness(db, provider, plans, new TestTimeProvider(Now));
        }

        public PlanCatalog Plan(string package, string plan) =>
            _plans.Single(p => p.PackageKey == package && p.PlanKey == plan);

        public async Task<Tenant> AddTenantAsync(string name)
        {
            var tenant = new Tenant(name, Guid.NewGuid().ToString("N")[..14]);
            Db.Tenants.Add(tenant);
            await Db.SaveChangesAsync();
            return tenant;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}
