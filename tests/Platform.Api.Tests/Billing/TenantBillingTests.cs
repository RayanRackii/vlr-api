using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Authorization;
using Platform.Api.Common;
using Platform.Api.Jobs;
using Platform.Api.Modules.Billing.Controllers;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Billing;

public sealed class TenantBillingTests
{
    private const string RootKey = "$aact_root_SECRET_KEY_BILLING_000";

    private static DateTimeOffset DueAt(DateOnly date) => BrazilTimeZone.AtLocal(date, TimeOnly.MinValue);

    // --- checkout ---

    [Fact]
    public async Task Checkout_creates_the_customer_and_subscription_and_returns_the_pix_invoice()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Trial Club", trial: true);
        var completo = h.Plan("reservas", "completo");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing, periodEnd: tenant.TrialEndsAt);

        var response = await h.Billing().CheckoutAsync(
            tenant.Id, "staff@club.test", Request(completo.Id, "annual", "pix"), CancellationToken.None);

        Assert.StartsWith("https://invoice.test/", response.InvoiceUrl);
        Assert.Equal("QUJD", response.PixQrCode);
        Assert.Equal("00020126pix-copy-paste", response.PixCopyPaste);
        Assert.Equal(1, h.Gateway.CustomerCreates);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);

        var request = Assert.IsType<BillingSubscriptionRequest>(h.Gateway.LastSubscriptionRequest);
        Assert.Equal(tenant.Id.ToString(), request.ExternalReference);
        Assert.Equal(BillingCycleKind.Yearly, request.Cycle);
        Assert.Equal(BillingMethod.Pix, request.Method);
        Assert.Equal(2990m, request.Value);
        // The trial is honoured: the first charge is due when it ends.
        Assert.Equal(BrazilTimeZone.GetCivilDate(tenant.TrialEndsAt!.Value), request.NextDueDate);
        var customer = Assert.IsType<BillingCustomerRequest>(h.Gateway.LastCustomerRequest);
        Assert.Equal(tenant.Id.ToString(), customer.ExternalReference);
        Assert.Equal("12345678000199", customer.CpfCnpj);
        Assert.Equal("owner@club.test", customer.Email);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(BillingModes.Provider, reloaded.BillingMode);
        Assert.Equal("asaas", reloaded.Provider);
        Assert.Equal(h.Gateway.Subscriptions.Keys.Single(), reloaded.ProviderSubscriptionId);
        Assert.NotNull(reloaded.ProviderCustomerId);
        Assert.Equal(completo.Id, reloaded.PendingPlanCatalogId);
        Assert.Equal(BillingCycles.Annual, reloaded.PendingBillingCycle);
        // Nothing is in force until the first payment: the trial plan and status are unchanged.
        Assert.Equal(SubscriptionStatuses.Trialing, reloaded.Status);
        Assert.Equal(h.Plan("reservas", "profissional").Id, reloaded.PlanCatalogId);
        Assert.Single(await h.EventsAsync(subscription.Id));
    }

    [Fact]
    public async Task Checkout_uses_the_value_after_the_discount_and_boleto_has_no_qr_code()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        var subscription = await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Trialing);
        subscription.DiscountPercent = 40;
        subscription.DiscountUntil = BillingHarness.Now.AddMonths(12);
        await h.Db.SaveChangesAsync();

        var response = await h.Billing().CheckoutAsync(
            tenant.Id, null, Request(plan.Id, "monthly", "boleto"), CancellationToken.None);

        Assert.Null(response.PixQrCode);
        Assert.Null(response.PixCopyPaste);
        Assert.Equal(113.40m, h.Gateway.LastSubscriptionRequest!.Value);
        Assert.Equal(BillingMethod.Boleto, h.Gateway.LastSubscriptionRequest.Method);
        // Same plan and cycle as the trial row: nothing pending.
        Assert.Null((await h.ReloadAsync(subscription.Id)).PendingPlanCatalogId);
    }

    [Fact]
    public async Task A_repeated_checkout_never_creates_a_second_provider_subscription()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "completo");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing);

        var first = await h.Billing().CheckoutAsync(tenant.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None);
        var second = await h.Billing().CheckoutAsync(tenant.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None);

        Assert.Equal(1, h.Gateway.SubscriptionCreates);
        Assert.Equal(first.InvoiceUrl, second.InvoiceUrl);

        // Another plan while a subscription exists is a plan change, not a second subscription.
        var ex = await Assert.ThrowsAsync<BillingException>(() => h.Billing().CheckoutAsync(
            tenant.Id, null, Request(h.Plan("reservas", "essencial").Id, "monthly", "pix"), CancellationToken.None));
        Assert.Equal(BillingErrorCodes.AlreadySubscribed, ex.Code);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
    }

    [Fact]
    public async Task A_lost_answer_is_retry_safe_because_the_provider_subscription_is_found_not_recreated()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        var subscription = await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Trialing);

        // First attempt: the provider created everything but the local save never happened (same as a crash).
        var customerId = await h.Gateway.CreateOrGetCustomerAsync(
            new BillingCustomerRequest("n", "e@e.test", "12345678000199", tenant.Id.ToString()));
        await h.Gateway.CreateSubscriptionAsync(new BillingSubscriptionRequest(
            customerId, BillingCycleKind.Monthly, BillingMethod.Pix, 189m, BillingHarness.Today, "d", tenant.Id.ToString()));

        await h.Billing().CheckoutAsync(tenant.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None);

        Assert.Equal(1, h.Gateway.CustomerCreates);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(h.Gateway.Subscriptions.Keys.Single(), reloaded.ProviderSubscriptionId);
    }

    [Fact]
    public async Task Checkout_idempotency_key_makes_a_retry_return_the_first_answer_with_one_provider_create()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "completo");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing);
        h.TenantProvider.TenantId = tenant.Id;
        var controller = Controller(h, tenant.Id);
        var body = Request(plan.Id, "monthly", "pix");

        var first = await controller.Checkout(body, CancellationToken.None, "key-1");
        var second = await controller.Checkout(body, CancellationToken.None, "key-1");

        var firstBody = Assert.IsType<CheckoutResponseDto>(Assert.IsType<OkObjectResult>(first.Result).Value);
        var secondBody = Assert.IsType<CheckoutResponseDto>(Assert.IsType<OkObjectResult>(second.Result).Value);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
        Assert.Equal(firstBody.InvoiceUrl, secondBody.InvoiceUrl);
        Assert.Equal(firstBody.PixCopyPaste, secondBody.PixCopyPaste);
        Assert.Equal(1, await h.Db.IdempotencyKeys.IgnoreQueryFilters().CountAsync(k => k.Scope == BillingController.CheckoutIdempotencyScope));
    }

    [Fact]
    public async Task Checkout_idempotency_key_reused_with_another_body_is_a_conflict_and_a_failed_try_can_be_retried()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "completo");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing);
        h.TenantProvider.TenantId = tenant.Id;
        var controller = Controller(h, tenant.Id);

        h.Gateway.Failure = new PaymentGatewayException("gateway_unreachable", 0);
        var failed = await controller.Checkout(Request(plan.Id, "monthly", "pix"), CancellationToken.None, "key-2");
        Assert.Equal(502, Assert.IsType<ObjectResult>(failed.Result).StatusCode);

        // The claim was released: the same key works once the provider is back.
        h.Gateway.Failure = null;
        var retried = await controller.Checkout(Request(plan.Id, "monthly", "pix"), CancellationToken.None, "key-2");
        Assert.IsType<OkObjectResult>(retried.Result);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);

        var reused = await controller.Checkout(Request(plan.Id, "annual", "pix"), CancellationToken.None, "key-2");
        Assert.IsType<ConflictObjectResult>(reused.Result);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
    }

    [Fact]
    public async Task Checkout_validates_the_input_and_the_tenant_data_before_calling_the_provider()
    {
        await using var h = await BillingHarness.CreateAsync();
        var plan = h.Plan("reservas", "profissional");
        var tenant = await h.AddTenantAsync("Club", contactEmail: null);
        await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Trialing);
        var noSubscription = await h.AddTenantAsync("No Sub", taxId: "33333333000191");
        var badTaxId = await h.AddTenantAsync("Bad Tax", taxId: "123");
        await h.AddSubscriptionAsync(badTaxId, plan, SubscriptionStatuses.Trialing);
        var service = h.Billing();

        async Task<string> CodeOf(Guid tenantId, string? actorEmail, CheckoutRequestDto request) =>
            (await Assert.ThrowsAsync<BillingException>(
                () => service.CheckoutAsync(tenantId, actorEmail, request, CancellationToken.None))).Code;

        Assert.Equal(BillingErrorCodes.InvalidCycle, await CodeOf(tenant.Id, null, Request(plan.Id, "weekly", "pix")));
        Assert.Equal(BillingErrorCodes.InvalidMethod, await CodeOf(tenant.Id, null, Request(plan.Id, "monthly", "cash")));
        Assert.Equal(BillingErrorCodes.PlanNotFound, await CodeOf(tenant.Id, null, Request(Guid.NewGuid(), "monthly", "pix")));
        Assert.Equal(BillingErrorCodes.NoSubscription, await CodeOf(noSubscription.Id, null, Request(plan.Id, "monthly", "pix")));
        Assert.Equal(BillingErrorCodes.ContactEmailRequired, await CodeOf(tenant.Id, null, Request(plan.Id, "monthly", "pix")));
        Assert.Equal(BillingErrorCodes.TaxIdInvalid, await CodeOf(badTaxId.Id, null, Request(plan.Id, "monthly", "pix")));
        Assert.Equal(0, h.Gateway.CustomerCreates);
        Assert.Equal(0, h.Gateway.SubscriptionCreates);

        // Without a company e-mail the staff user's own e-mail is used.
        await service.CheckoutAsync(tenant.Id, "staff@club.test", Request(plan.Id, "monthly", "pix"), CancellationToken.None);
        Assert.Equal("staff@club.test", h.Gateway.LastCustomerRequest!.Email);
    }

    [Fact]
    public async Task Checkout_on_a_manual_row_moves_it_to_provider_billing_unless_nothing_would_be_charged()
    {
        await using var h = await BillingHarness.CreateAsync();
        var plan = h.Plan("reservas", "profissional");
        var tenant = await h.AddTenantAsync("Pilot");
        var manual = await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Active, BillingModes.Manual);
        var free = await h.AddTenantAsync("Free", taxId: "44444444000191");
        var freeRow = await h.AddSubscriptionAsync(free, plan, SubscriptionStatuses.Active, BillingModes.Manual);
        freeRow.DiscountPercent = 100;
        await h.Db.SaveChangesAsync();

        await h.Billing().CheckoutAsync(tenant.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<BillingException>(
            () => h.Billing().CheckoutAsync(free.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None));

        Assert.Equal(BillingModes.Provider, (await h.ReloadAsync(manual.Id)).BillingMode);
        Assert.Equal(BillingErrorCodes.NothingToCharge, ex.Code);
        Assert.Equal(BillingModes.Manual, (await h.ReloadAsync(freeRow.Id)).BillingMode);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
    }

    [Fact]
    public async Task The_root_key_never_reaches_a_response_an_exception_or_a_log()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Trialing);
        h.TenantProvider.TenantId = tenant.Id;

        var leaky = $$"""{"errors":[{"code":"invalid_subscription","description":"key {{RootKey}} rejected"}]}""";
        var handler = new QueueHandler(
            Json(HttpStatusCode.OK, """{"data":[]}"""),
            Json(HttpStatusCode.OK, """{"id":"cus_1"}"""),
            Json(HttpStatusCode.OK, """{"data":[]}"""),
            Json(HttpStatusCode.BadRequest, leaky));
        var gatewayLogs = new CapturingLogger<AsaasGateway>();
        var serviceLogs = new CapturingLogger<TenantBillingService>();
        var gateway = new AsaasGateway(
            new HttpClient(handler),
            new StaticAsaasOptions(new AsaasOptions { BaseUrl = "https://api-sandbox.asaas.com/v3", RootApiKey = RootKey }),
            gatewayLogs);
        var controller = new BillingController(
            h.Read(),
            h.Billing(gateway, serviceLogs),
            h.TenantProvider,
            new FakeActorAccessor(tenant.Id, Guid.NewGuid()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var result = await controller.Checkout(Request(plan.Id, "monthly", "pix"), CancellationToken.None);

        var error = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(502, error.StatusCode);
        var wire = JsonSerializer.Serialize(error.Value);
        Assert.Contains(BillingErrorCodes.ProviderError, wire);
        Assert.DoesNotContain(RootKey, wire);
        Assert.DoesNotContain("description", wire);
        Assert.All(handler.AccessTokens, token => Assert.Equal(RootKey, token));
        Assert.DoesNotContain(gatewayLogs.Messages.Concat(serviceLogs.Messages), m => m.Contains(RootKey, StringComparison.Ordinal));
        Assert.NotEmpty(gatewayLogs.Messages);

        // The key is also absent from the stored history and from any DTO the tenant can read.
        var subscription = await h.Read().GetSubscriptionAsync(tenant.Id, CancellationToken.None);
        Assert.DoesNotContain(RootKey, JsonSerializer.Serialize(subscription));
    }

    // --- change plan ---

    [Fact]
    public async Task An_upgrade_applies_now_updates_the_provider_and_only_enables_missing_modules()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "essencial"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        h.Db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Rentals));
        h.Db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Pmoc));
        h.Db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Inventory, isActive: false));
        await h.Db.SaveChangesAsync();
        var completo = h.Plan("reservas", "completo");

        await h.Billing().ChangePlanAsync(
            tenant.Id, new ChangePlanRequestDto { PlanCatalogId = completo.Id, BillingCycle = "monthly" }, CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(completo.Id, reloaded.PlanCatalogId);
        Assert.Null(reloaded.PendingPlanCatalogId);
        var update = Assert.Single(h.Gateway.Updates);
        Assert.Equal(299m, update.Value);
        Assert.True(update.UpdatePendingPayments);

        var modules = await h.Db.TenantModules.AsNoTracking().Where(m => m.TenantId == tenant.Id).ToListAsync();
        Assert.Contains(modules, m => m.ModuleName == PlatformModules.Catalog && m.IsActive);
        Assert.Contains(modules, m => m.ModuleName == PlatformModules.Rentals && m.IsActive);
        // Never disables or touches modules the plan does not mention.
        Assert.Contains(modules, m => m.ModuleName == PlatformModules.Pmoc && m.IsActive);
        Assert.Contains(modules, m => m.ModuleName == PlatformModules.Inventory && !m.IsActive);
        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(SubscriptionEventTypes.PlanChanged, stored.Type);
    }

    [Fact]
    public async Task A_downgrade_within_usage_is_scheduled_for_the_period_end()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var completo = h.Plan("reservas", "completo");
        var essencial = h.Plan("reservas", "essencial");
        var subscription = await h.AddSubscriptionAsync(
            tenant, completo, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1", periodEnd: Now(20));
        SeedUsers(h.Db, tenant.Id, 3);
        await h.Db.SaveChangesAsync();

        await h.Billing().ChangePlanAsync(
            tenant.Id, new ChangePlanRequestDto { PlanCatalogId = essencial.Id, BillingCycle = "monthly" }, CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(completo.Id, reloaded.PlanCatalogId);
        Assert.Equal(essencial.Id, reloaded.PendingPlanCatalogId);
        Assert.Equal(BillingCycles.Monthly, reloaded.PendingBillingCycle);
        var update = Assert.Single(h.Gateway.Updates);
        Assert.Equal(99m, update.Value);
        Assert.False(update.UpdatePendingPayments);
    }

    [Fact]
    public async Task A_downgrade_below_current_usage_is_refused_with_409_and_changes_nothing()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "completo"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        SeedUsers(h.Db, tenant.Id, 5);
        await h.Db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BillingException>(() => h.Billing().ChangePlanAsync(
            tenant.Id,
            new ChangePlanRequestDto { PlanCatalogId = h.Plan("reservas", "essencial").Id, BillingCycle = "monthly" },
            CancellationToken.None));

        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal("plan.usage_above_limit", ex.Code);
        Assert.Equal(PlanLimitKeys.Users, ex.Details["limit"]);
        Assert.Equal(5, ex.Details["current"]);
        Assert.Equal(3, ex.Details["max"]);
        Assert.Equal("essencial", ex.Details["planKey"]);
        Assert.Empty(h.Gateway.Updates);
        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Null(reloaded.PendingPlanCatalogId);
        Assert.Empty(await h.EventsAsync(subscription.Id));
    }

    [Fact]
    public async Task A_downgrade_is_checked_against_usage_unless_the_row_is_unlimited()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "completo"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        SeedUsers(h.Db, tenant.Id, 20);
        await h.Db.SaveChangesAsync();
        var request = new ChangePlanRequestDto { PlanCatalogId = h.Plan("reservas", "essencial").Id, BillingCycle = "monthly" };

        await Assert.ThrowsAsync<BillingException>(() => h.Billing().ChangePlanAsync(tenant.Id, request, CancellationToken.None));

        subscription.LimitsUnlimited = true;
        await h.Db.SaveChangesAsync();
        await h.Billing().ChangePlanAsync(tenant.Id, request, CancellationToken.None);
        Assert.Equal(h.Plan("reservas", "essencial").Id, (await h.ReloadAsync(subscription.Id)).PendingPlanCatalogId);
    }

    [Fact]
    public async Task Choosing_the_current_plan_again_cancels_a_scheduled_downgrade()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var completo = h.Plan("reservas", "completo");
        var subscription = await h.AddSubscriptionAsync(
            tenant, completo, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        subscription.PendingPlanCatalogId = h.Plan("reservas", "essencial").Id;
        subscription.PendingBillingCycle = BillingCycles.Monthly;
        await h.Db.SaveChangesAsync();

        await h.Billing().ChangePlanAsync(
            tenant.Id, new ChangePlanRequestDto { PlanCatalogId = completo.Id, BillingCycle = "monthly" }, CancellationToken.None);

        Assert.Null((await h.ReloadAsync(subscription.Id)).PendingPlanCatalogId);
        Assert.Equal(299m, Assert.Single(h.Gateway.Updates).Value);
    }

    [Theory]
    [InlineData(SubscriptionStatuses.Trialing, BillingModes.Provider)]
    [InlineData(SubscriptionStatuses.Active, BillingModes.Manual)]
    [InlineData(SubscriptionStatuses.ReadOnly, BillingModes.Provider)]
    public async Task Change_plan_needs_an_active_subscription_billed_through_the_provider(string status, string mode)
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "essencial"), status, mode, providerSubscriptionId: "sub_1");

        var ex = await Assert.ThrowsAsync<BillingException>(() => h.Billing().ChangePlanAsync(
            tenant.Id,
            new ChangePlanRequestDto { PlanCatalogId = h.Plan("reservas", "completo").Id, BillingCycle = "monthly" },
            CancellationToken.None));

        Assert.Equal(BillingErrorCodes.NoActiveSubscription, ex.Code);
        Assert.Empty(h.Gateway.Updates);
    }

    [Fact]
    public async Task A_provider_failure_during_an_upgrade_leaves_the_subscription_unchanged()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var essencial = h.Plan("reservas", "essencial");
        var subscription = await h.AddSubscriptionAsync(
            tenant, essencial, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        h.Gateway.Failure = new PaymentGatewayException("invalid_action", 400);

        var ex = await Assert.ThrowsAsync<BillingException>(() => h.Billing().ChangePlanAsync(
            tenant.Id,
            new ChangePlanRequestDto { PlanCatalogId = h.Plan("reservas", "completo").Id, BillingCycle = "monthly" },
            CancellationToken.None));

        Assert.Equal(502, ex.HttpStatus);
        Assert.Equal(essencial.Id, (await h.ReloadAsync(subscription.Id)).PlanCatalogId);
    }

    // --- cancel ---

    [Fact]
    public async Task Cancel_keeps_the_plan_until_the_period_ends_then_the_job_makes_it_read_only()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1", periodEnd: Now(10));
        var service = h.Billing();

        await service.CancelAsync(tenant.Id, CancellationToken.None);
        await service.CancelAsync(tenant.Id, CancellationToken.None);

        var cancelled = await h.ReloadAsync(subscription.Id);
        Assert.Equal(1, h.Gateway.Cancels);
        Assert.Equal(SubscriptionStatuses.Active, cancelled.Status);
        Assert.True(cancelled.CancelAtPeriodEnd);
        Assert.NotNull(cancelled.CanceledAt);
        Assert.Equal(SubscriptionEventTypes.Canceled, Assert.Single(await h.EventsAsync(subscription.Id)).Type);

        // The provider echoes SUBSCRIPTION_DELETED: still no change.
        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);
        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(subscription.Id)).Status);

        var job = new BillingPeriodEndJob(h.Db, h.Time, NullLogger<BillingPeriodEndJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(subscription.Id)).Status);

        h.Time.Advance(TimeSpan.FromDays(11));
        await job.ExecuteAsync(CancellationToken.None);
        var closed = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.ReadOnly, closed.Status);
        Assert.Contains(await h.EventsAsync(subscription.Id), e => e.Type == SubscriptionEventTypes.ReadOnly);
    }

    [Fact]
    public async Task The_period_end_job_never_touches_manual_rows_or_subscriptions_that_were_not_cancelled()
    {
        await using var h = await BillingHarness.CreateAsync();
        var plan = h.Plan("reservas", "profissional");
        var manualTenant = await h.AddTenantAsync("Manual", taxId: "55555555000191");
        var manual = await h.AddSubscriptionAsync(manualTenant, plan, SubscriptionStatuses.Active, BillingModes.Manual, periodEnd: Now(-5));
        manual.CancelAtPeriodEnd = true;
        var activeTenant = await h.AddTenantAsync("Active", taxId: "66666666000191");
        var active = await h.AddSubscriptionAsync(activeTenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_a", periodEnd: Now(-5));
        await h.Db.SaveChangesAsync();

        await new BillingPeriodEndJob(h.Db, h.Time, NullLogger<BillingPeriodEndJob>.Instance).ExecuteAsync(CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(manual.Id)).Status);
        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(active.Id)).Status);
    }

    [Fact]
    public async Task Cancelling_an_unfinished_checkout_drops_the_provider_link_and_keeps_the_trial()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Trial", trial: true);
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing, providerSubscriptionId: "sub_1");

        await h.Billing().CancelAsync(tenant.Id, CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Trialing, reloaded.Status);
        Assert.Null(reloaded.ProviderSubscriptionId);
        Assert.False(reloaded.CancelAtPeriodEnd);
        Assert.Equal(1, h.Gateway.Cancels);
    }

    [Fact]
    public async Task Resubscribing_after_cancelling_creates_a_new_provider_subscription_due_when_the_paid_period_ends()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        var subscription = await h.AddSubscriptionAsync(
            tenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1", periodEnd: Now(10));
        await h.Billing().CancelAsync(tenant.Id, CancellationToken.None);

        await h.Billing().CheckoutAsync(tenant.Id, null, Request(plan.Id, "monthly", "pix"), CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.NotEqual("sub_1", reloaded.ProviderSubscriptionId);
        Assert.False(reloaded.CancelAtPeriodEnd);
        Assert.Equal(BrazilTimeZone.GetCivilDate(Now(10)), h.Gateway.LastSubscriptionRequest!.NextDueDate);
        Assert.Equal(1, h.Gateway.SubscriptionCreates);
    }

    // --- discount expiry ---

    [Fact]
    public async Task The_discount_expiry_job_moves_the_provider_to_the_full_price_then_clears_the_discount()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        var subscription = await h.AddSubscriptionAsync(
            tenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        subscription.DiscountPercent = 40;
        subscription.DiscountUntil = Now(-1);
        await h.Db.SaveChangesAsync();
        var job = new BillingDiscountExpiryJob(h.Db, h.Gateway, h.Time, NullLogger<BillingDiscountExpiryJob>.Instance);

        await job.ExecuteAsync(CancellationToken.None);
        await job.ExecuteAsync(CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(0, reloaded.DiscountPercent);
        Assert.Null(reloaded.DiscountUntil);
        var update = Assert.Single(h.Gateway.Updates);
        Assert.Equal(189m, update.Value);
        Assert.Equal(BillingCycleKind.Monthly, update.Cycle);
        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(SubscriptionEventTypes.PlanChanged, stored.Type);
        Assert.Contains("discount_expiry", stored.PayloadJson);
    }

    [Fact]
    public async Task The_discount_expiry_job_prices_a_scheduled_downgrade_and_keeps_the_discount_when_the_provider_fails()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "completo"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        subscription.DiscountPercent = 40;
        subscription.DiscountUntil = Now(-1);
        subscription.PendingPlanCatalogId = h.Plan("reservas", "essencial").Id;
        subscription.PendingBillingCycle = BillingCycles.Monthly;
        await h.Db.SaveChangesAsync();
        var job = new BillingDiscountExpiryJob(h.Db, h.Gateway, h.Time, NullLogger<BillingDiscountExpiryJob>.Instance);

        h.Gateway.Failure = new PaymentGatewayException("gateway_unreachable", 0);
        await job.ExecuteAsync(CancellationToken.None);
        var failed = await h.ReloadAsync(subscription.Id);
        Assert.Equal(40, failed.DiscountPercent);
        Assert.NotNull(failed.DiscountUntil);
        Assert.Empty(await h.EventsAsync(subscription.Id));

        // The next run retries and prices the plan that is about to start.
        h.Gateway.Failure = null;
        await job.ExecuteAsync(CancellationToken.None);
        Assert.Equal(0, (await h.ReloadAsync(subscription.Id)).DiscountPercent);
        Assert.Equal(99m, Assert.Single(h.Gateway.Updates).Value);
    }

    [Fact]
    public async Task The_discount_expiry_job_clears_manual_rows_without_calling_the_provider_and_skips_running_discounts()
    {
        await using var h = await BillingHarness.CreateAsync();
        var plan = h.Plan("reservas", "profissional");
        var manualTenant = await h.AddTenantAsync("Manual", taxId: "77777777000191");
        var manual = await h.AddSubscriptionAsync(manualTenant, plan, SubscriptionStatuses.Active, BillingModes.Manual);
        manual.DiscountPercent = 100;
        manual.DiscountUntil = Now(-2);
        var runningTenant = await h.AddTenantAsync("Running", taxId: "88888888000191");
        var running = await h.AddSubscriptionAsync(runningTenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_r");
        running.DiscountPercent = 40;
        running.DiscountUntil = Now(30);
        var foreverTenant = await h.AddTenantAsync("Forever", taxId: "99999999000191");
        var forever = await h.AddSubscriptionAsync(foreverTenant, plan, SubscriptionStatuses.Active, BillingModes.Manual);
        forever.DiscountPercent = 100;
        await h.Db.SaveChangesAsync();

        await new BillingDiscountExpiryJob(h.Db, h.Gateway, h.Time, NullLogger<BillingDiscountExpiryJob>.Instance)
            .ExecuteAsync(CancellationToken.None);

        Assert.Equal(0, (await h.ReloadAsync(manual.Id)).DiscountPercent);
        Assert.Equal(40, (await h.ReloadAsync(running.Id)).DiscountPercent);
        Assert.Equal(100, (await h.ReloadAsync(forever.Id)).DiscountPercent);
        Assert.Empty(h.Gateway.Updates);
    }

    // --- read side and authorization ---

    [Fact]
    public async Task The_subscription_read_returns_only_the_own_tenant_with_usage_limits_and_history()
    {
        await using var h = await BillingHarness.CreateAsync();
        var a = await h.AddTenantAsync("Club A", taxId: "11111111000191");
        var b = await h.AddTenantAsync("Club B", taxId: "22222222000191");
        var subA = await h.AddSubscriptionAsync(
            a, h.Plan("reservas", "essencial"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a", periodEnd: Now(10));
        await h.AddSubscriptionAsync(b, h.Plan("locacao", "completo"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_b");
        SeedUsers(h.Db, a.Id, 2);
        SeedUsers(h.Db, b.Id, 9);
        h.Db.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = a.Id,
            SubscriptionId = subA.Id,
            Type = SubscriptionEventTypes.PaymentReceived,
            PayloadJson = """{"valueCents":9900,"dueDate":"2026-12-10"}""",
        });
        await h.Db.SaveChangesAsync();

        var dto = await h.Read().GetSubscriptionAsync(a.Id, CancellationToken.None);

        Assert.True(dto.HasSubscription);
        Assert.Equal("reservas", dto.PackageKey);
        Assert.Equal("essencial", dto.PlanKey);
        Assert.Equal(2, dto.Usage!.Users);
        Assert.Equal(new PlanLimitsDto(null, 3, 3, 1, 0), dto.PlanLimits);
        Assert.Equal(Now(10), dto.NextChargeAt);
        Assert.Equal(9900, dto.ChargeCents);
        var history = Assert.Single(dto.Events);
        Assert.Equal(9900, history.ValueCents);
        Assert.Equal(new DateOnly(2026, 12, 10), history.DueDate);

        var none = await h.Read().GetSubscriptionAsync(Guid.NewGuid(), CancellationToken.None);
        Assert.False(none.HasSubscription);
        Assert.Empty(none.Events);
    }

    [Fact]
    public async Task The_plans_read_lists_active_plans_and_marks_the_current_one()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        h.Plan("locacao", "essencial").IsActive = false;
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");

        var dto = await h.Read().GetPlansAsync(tenant.Id, CancellationToken.None);

        Assert.Equal(h.Plan("reservas", "profissional").Id, dto.CurrentPlanCatalogId);
        Assert.Equal("reservas", dto.CurrentPackageKey);
        Assert.Equal(8, dto.Plans.Count);
        Assert.DoesNotContain(dto.Plans, p => p.PackageKey == "locacao" && p.PlanKey == "essencial");
    }

    [Fact]
    public void Staff_endpoints_need_the_billing_permission_and_the_webhook_is_anonymous()
    {
        var expected = PermissionPolicies.Name(Permissions.Core.SettingsBilling);
        var actions = typeof(BillingController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes().Any(a => a is Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute))
            .ToList();

        Assert.Equal(5, actions.Count);
        Assert.All(actions, method =>
            Assert.Equal(expected, method.GetCustomAttribute<RequirePermissionAttribute>()!.Policy));
        Assert.Null(typeof(BillingController).GetCustomAttribute<AllowAnonymousAttribute>());

        var receive = typeof(AsaasBillingWebhookController).GetMethod(nameof(AsaasBillingWebhookController.Receive))!;
        Assert.NotNull(receive.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(receive.GetCustomAttribute<RequirePermissionAttribute>());
    }

    // --- helpers ---

    private static DateTimeOffset Now(int days) => BillingHarness.Now.AddDays(days);

    private static CheckoutRequestDto Request(Guid planId, string cycle, string method) =>
        new() { PlanCatalogId = planId, BillingCycle = cycle, Method = method };

    private static void SeedUsers(AppDbContext db, Guid tenantId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            db.Users.Add(new User(tenantId, Guid.NewGuid().ToString(), $"Member {i}", $"{tenantId:N}-{i}@club.test"));
        }
    }

    private static BillingController Controller(BillingHarness h, Guid tenantId)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "staff@club.test")], "test")),
        };
        return new BillingController(
            h.Read(),
            h.Billing(),
            h.TenantProvider,
            new FakeActorAccessor(tenantId, Guid.NewGuid()),
            new IdempotencyService(h.Db, h.TenantProvider, h.Time))
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue;

        public QueueHandler(params HttpResponseMessage[] responses) => _queue = new Queue<HttpResponseMessage>(responses);

        public List<string?> AccessTokens { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AccessTokens.Add(request.Headers.TryGetValues("access_token", out var values) ? values.Single() : null);
            return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : throw new InvalidOperationException("No response queued."));
        }
    }
}
