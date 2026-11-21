using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Billing.Controllers;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Billing;

public sealed class BillingWebhookTests
{
    private const string Token = "billing-webhook-token-0123456789";

    private static DateTimeOffset DueAt(DateOnly date) => BrazilTimeZone.AtLocal(date, TimeOnly.MinValue);

    // --- authentication and intake ---

    [Fact]
    public async Task A_bad_or_missing_token_is_unauthorized_and_queues_nothing()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        var body = BillingHarness.PaymentPayload("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", "cus_of_sub_1");
        var intake = h.Intake(Token);

        Assert.Equal(BillingWebhookOutcome.Unauthorized, await intake.AcceptAsync("wrong-token", body, CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Unauthorized, await intake.AcceptAsync(null, body, CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Unauthorized, await intake.AcceptAsync(string.Empty, body, CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Unauthorized, await intake.AcceptAsync(Token + "x", body, CancellationToken.None));
        Assert.Empty(h.Scheduler.Calls);
    }

    [Fact]
    public async Task The_configured_token_queues_one_job_with_the_event_data()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");

        var outcome = await h.Intake(Token).AcceptAsync(
            Token,
            BillingHarness.PaymentPayload("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", "cus_of_sub_1"),
            CancellationToken.None);

        Assert.Equal(BillingWebhookOutcome.Accepted, outcome);
        Assert.Equal(("evt_1", "PAYMENT_RECEIVED", (string?)"pay_1", (string?)"sub_1"), Assert.Single(h.Scheduler.Calls));
    }

    [Theory]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    public async Task Without_a_configured_token_only_development_and_testing_accept(string environment, bool accepted)
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");

        var outcome = await h.Intake(configuredToken: null, environment).AcceptAsync(
            null,
            BillingHarness.PaymentPayload("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", "cus_of_sub_1"),
            CancellationToken.None);

        Assert.Equal(accepted ? BillingWebhookOutcome.Accepted : BillingWebhookOutcome.Unauthorized, outcome);
    }

    [Fact]
    public async Task Unknown_events_malformed_bodies_and_unknown_subscriptions_are_ignored()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        var intake = h.Intake(Token);

        Assert.Equal(BillingWebhookOutcome.Ignored, await intake.AcceptAsync(Token, "not json", CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Ignored, await intake.AcceptAsync(Token, "[]", CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Ignored, await intake.AcceptAsync(
            Token, BillingHarness.PaymentPayload("evt_2", "PAYMENT_CREATED", "pay_1", "sub_1", "cus_of_sub_1"), CancellationToken.None));
        Assert.Equal(BillingWebhookOutcome.Ignored, await intake.AcceptAsync(
            Token, BillingHarness.PaymentPayload("evt_3", "PAYMENT_RECEIVED", "pay_1", "sub_unknown", "cus_x"), CancellationToken.None));
        Assert.Empty(h.Scheduler.Calls);
    }

    [Fact]
    public async Task Intake_never_trusts_the_payload_tenant_a_customer_that_does_not_match_is_ignored()
    {
        await using var h = await BillingHarness.CreateAsync();
        var a = await h.AddTenantAsync("Club A", taxId: "11111111000191");
        var b = await h.AddTenantAsync("Club B", taxId: "22222222000191");
        await h.AddSubscriptionAsync(a, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a", providerCustomerId: "cus_a");
        await h.AddSubscriptionAsync(b, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_b", providerCustomerId: "cus_b");

        // The body names tenant A's subscription but tenant B's customer.
        var outcome = await h.Intake(Token).AcceptAsync(
            Token,
            BillingHarness.PaymentPayload("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_a", "cus_b"),
            CancellationToken.None);

        Assert.Equal(BillingWebhookOutcome.Ignored, outcome);
        Assert.Empty(h.Scheduler.Calls);
    }

    [Fact]
    public async Task Manual_rows_are_never_touched_by_the_webhook()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Pilot Club");
        var plan = h.Plan("reservas", "profissional");
        var manual = await h.AddSubscriptionAsync(
            tenant, plan, SubscriptionStatuses.Active, BillingModes.Manual, providerSubscriptionId: "sub_m");
        h.Gateway.AddPayment("pay_m", "sub_m", "cus_of_sub_m", GatewayChargeStatus.Paid, BillingHarness.Today);

        var outcome = await h.Intake(Token).AcceptAsync(
            Token,
            BillingHarness.PaymentPayload("evt_m", "PAYMENT_RECEIVED", "pay_m", "sub_m", "cus_of_sub_m"),
            CancellationToken.None);
        await h.Processor().ProcessAsync("evt_m", "PAYMENT_RECEIVED", "pay_m", "sub_m", CancellationToken.None);
        await h.Processor().ProcessAsync("evt_m2", "SUBSCRIPTION_DELETED", null, "sub_m", CancellationToken.None);
        h.Gateway.Subscriptions["sub_m"] = h.Gateway.Subscriptions["sub_m"] with { Deleted = true, State = BillingSubscriptionState.Inactive };
        await h.Processor().ProcessAsync("evt_m3", "SUBSCRIPTION_DELETED", null, "sub_m", CancellationToken.None);

        Assert.Equal(BillingWebhookOutcome.Ignored, outcome);
        var reloaded = await h.ReloadAsync(manual.Id);
        Assert.Equal(SubscriptionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.LastPaymentAt);
        Assert.Null(reloaded.CurrentPeriodEnd);
        Assert.Empty(await h.EventsAsync(manual.Id));
    }

    [Fact]
    public async Task The_controller_answers_401_for_a_rejected_token_and_200_otherwise()
    {
        foreach (var (outcome, expectUnauthorized) in new[]
        {
            (BillingWebhookOutcome.Unauthorized, true),
            (BillingWebhookOutcome.Accepted, false),
            (BillingWebhookOutcome.Ignored, false),
        })
        {
            var intake = new FixedIntake(outcome);
            var http = new DefaultHttpContext();
            http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            http.Request.Headers[BillingWebhookEvents.TokenHeader] = "presented";
            var controller = new AsaasBillingWebhookController(intake, NullLogger<AsaasBillingWebhookController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
            };

            var result = await controller.Receive(CancellationToken.None);

            Assert.Equal("presented", intake.PresentedToken);
            if (expectUnauthorized)
            {
                Assert.IsType<UnauthorizedObjectResult>(result);
            }
            else
            {
                Assert.IsType<OkResult>(result);
            }
        }
    }

    [Fact]
    public async Task The_controller_rejects_an_oversized_payload_before_the_intake()
    {
        var intake = new FixedIntake(BillingWebhookOutcome.Accepted);
        var http = new DefaultHttpContext();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', AsaasBillingWebhookController.MaxPayloadChars + 10)));
        var controller = new AsaasBillingWebhookController(intake, NullLogger<AsaasBillingWebhookController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.Receive(CancellationToken.None);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.False(intake.Called);
    }

    // --- PAYMENT_RECEIVED / CONFIRMED ---

    [Fact]
    public async Task Received_activates_extends_the_period_converts_the_trial_and_applies_the_checkout_plan()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Trial Club", trial: true);
        var profissional = h.Plan("reservas", "profissional");
        var completo = h.Plan("reservas", "completo");
        var subscription = await h.AddSubscriptionAsync(
            tenant, profissional, SubscriptionStatuses.Trialing, providerSubscriptionId: "sub_1", periodEnd: tenant.TrialEndsAt);
        subscription.PendingPlanCatalogId = completo.Id;
        subscription.PendingBillingCycle = BillingCycles.Annual;
        await h.Db.SaveChangesAsync();
        h.Gateway.AddPayment("pay_1", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today, 2990m);

        await h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        var dueAt = DueAt(BillingHarness.Today);
        Assert.Equal(SubscriptionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.PastDueSince);
        Assert.Equal(dueAt, reloaded.LastPaymentAt);
        Assert.Equal(dueAt, reloaded.CurrentPeriodStart);
        Assert.Equal(dueAt.AddYears(1), reloaded.CurrentPeriodEnd);
        Assert.Equal(completo.Id, reloaded.PlanCatalogId);
        Assert.Equal(BillingCycles.Annual, reloaded.BillingCycle);
        Assert.Null(reloaded.PendingPlanCatalogId);
        Assert.Null(reloaded.PendingBillingCycle);

        var storedTenant = await h.Db.Tenants.AsNoTracking().SingleAsync(t => t.Id == tenant.Id);
        Assert.False(storedTenant.IsTrial);
        Assert.Null(storedTenant.TrialEndsAt);
        Assert.Null(storedTenant.TrialPurgeAt);

        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(SubscriptionEventTypes.PaymentReceived, stored.Type);
        Assert.Equal("evt_1", stored.ProviderEventId);
        Assert.DoesNotContain("card", stored.PayloadJson, StringComparison.OrdinalIgnoreCase);

        // The plan's modules were switched on (and nothing else touched).
        var modules = await h.Db.TenantModules.AsNoTracking().Where(m => m.TenantId == tenant.Id).ToListAsync();
        Assert.Contains(modules, m => m.ModuleName == PlatformModules.Catalog && m.IsActive);
        Assert.All(modules, m => Assert.True(m.IsActive));
    }

    [Fact]
    public async Task A_renewal_extends_from_its_due_date_and_never_shortens_the_period()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var plan = h.Plan("reservas", "profissional");
        var firstDue = new DateOnly(2026, 11, 10);
        var subscription = await h.AddSubscriptionAsync(
            tenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1",
            periodEnd: DueAt(BillingHarness.Today), lastPaymentAt: DueAt(firstDue));
        h.Gateway.AddPayment("pay_2", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_2", "PAYMENT_CONFIRMED", "pay_2", "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(DueAt(BillingHarness.Today).AddMonths(1), reloaded.CurrentPeriodEnd);
        Assert.Equal(DueAt(BillingHarness.Today), reloaded.LastPaymentAt);
    }

    [Theory]
    [InlineData(SubscriptionStatuses.ReadOnly)]
    [InlineData(SubscriptionStatuses.Canceled)]
    [InlineData(SubscriptionStatuses.PastDue)]
    public async Task Received_reactivates_a_blocked_or_overdue_subscription(string status)
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), status, providerSubscriptionId: "sub_1",
            lastPaymentAt: DueAt(new DateOnly(2026, 10, 10)));
        subscription.PastDueSince = Now(-5);
        subscription.CanceledAt = status == SubscriptionStatuses.Canceled ? Now(-1) : null;
        await h.Db.SaveChangesAsync();
        h.Gateway.AddPayment("pay_9", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_9", "PAYMENT_RECEIVED", "pay_9", "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.PastDueSince);
        Assert.Null(reloaded.CanceledAt);
        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(
            status == SubscriptionStatuses.PastDue ? SubscriptionEventTypes.PaymentReceived : SubscriptionEventTypes.Reactivated,
            stored.Type);
    }

    [Fact]
    public async Task A_scheduled_downgrade_starts_with_the_renewal_payment()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var completo = h.Plan("reservas", "completo");
        var essencial = h.Plan("reservas", "essencial");
        var lastDue = BillingHarness.Today.AddDays(-30);
        var subscription = await h.AddSubscriptionAsync(
            tenant, completo, SubscriptionStatuses.Active, providerSubscriptionId: "sub_1",
            periodEnd: DueAt(BillingHarness.Today), lastPaymentAt: DueAt(lastDue));
        subscription.PendingPlanCatalogId = essencial.Id;
        subscription.PendingBillingCycle = BillingCycles.Monthly;
        await h.Db.SaveChangesAsync();
        h.Gateway.AddPayment("pay_old", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, lastDue);
        h.Gateway.AddPayment("pay_renewal", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today);

        // A repeat of the payment of the period in force does not start the downgrade.
        await h.Processor().ProcessAsync("evt_a", "PAYMENT_RECEIVED", "pay_old", "sub_1", CancellationToken.None);
        Assert.Equal(completo.Id, (await h.ReloadAsync(subscription.Id)).PlanCatalogId);

        await h.Processor().ProcessAsync("evt_b", "PAYMENT_RECEIVED", "pay_renewal", "sub_1", CancellationToken.None);
        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(essencial.Id, reloaded.PlanCatalogId);
        Assert.Null(reloaded.PendingPlanCatalogId);
        Assert.Equal(DueAt(BillingHarness.Today).AddMonths(1), reloaded.CurrentPeriodEnd);
    }

    [Fact]
    public async Task A_payment_that_is_not_paid_at_the_provider_changes_nothing()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.ReadOnly, providerSubscriptionId: "sub_1");
        // The webhook claims RECEIVED but the provider still says pending: the re-fetch wins.
        h.Gateway.AddPayment("pay_1", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Pending, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(subscription.Id)).Status);
        Assert.Empty(await h.EventsAsync(subscription.Id));
    }

    [Fact]
    public async Task A_duplicate_event_is_processed_once_and_the_second_confirmation_of_a_payment_is_a_no_op()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.ReadOnly, providerSubscriptionId: "sub_1");
        h.Gateway.AddPayment("pay_1", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today);
        var body = BillingHarness.PaymentPayload("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", "cus_of_sub_1");

        await h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", CancellationToken.None);
        var afterFirst = await h.ReloadAsync(subscription.Id);
        var callsAfterFirst = h.Gateway.GetPaymentCalls;

        // Same event id again: dropped before any provider call.
        await h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", CancellationToken.None);
        // Asaas also sends CONFIRMED and RECEIVED for one card payment: a new event id for the same payment.
        await h.Processor().ProcessAsync("evt_1b", "PAYMENT_CONFIRMED", "pay_1", "sub_1", CancellationToken.None);
        var intake = await h.Intake(Token).AcceptAsync(Token, body, CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(callsAfterFirst + 1, h.Gateway.GetPaymentCalls);
        Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(afterFirst.CurrentPeriodEnd, reloaded.CurrentPeriodEnd);
        Assert.Equal(afterFirst.RowVersion, reloaded.RowVersion);
        Assert.Equal(BillingWebhookOutcome.Ignored, intake);
        Assert.Empty(h.Scheduler.Calls);
    }

    // --- PAYMENT_OVERDUE ---

    [Fact]
    public async Task Overdue_moves_a_paying_subscription_to_past_due_since_the_due_date()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1",
            lastPaymentAt: DueAt(new DateOnly(2026, 11, 10)));
        h.Gateway.AddPayment("pay_2", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Overdue, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_o", "PAYMENT_OVERDUE", "pay_2", "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.PastDue, reloaded.Status);
        Assert.Equal(DueAt(BillingHarness.Today), reloaded.PastDueSince);
        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(SubscriptionEventTypes.PaymentOverdue, stored.Type);
        Assert.Equal("evt_o", stored.ProviderEventId);
    }

    [Fact]
    public async Task An_out_of_order_overdue_never_overrides_a_newer_received_payment()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1",
            lastPaymentAt: DueAt(new DateOnly(2026, 11, 10)));
        // Month 1 paid; month 2 overdue notice is delayed and arrives after month 2 was paid.
        h.Gateway.AddPayment("pay_2", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Paid, BillingHarness.Today);
        await h.Processor().ProcessAsync("evt_received", "PAYMENT_RECEIVED", "pay_2", "sub_1", CancellationToken.None);

        // The provider still reports the older invoice as overdue (stale event about month 1).
        h.Gateway.AddPayment("pay_1", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Overdue, new DateOnly(2026, 11, 10));
        await h.Processor().ProcessAsync("evt_stale_overdue", "PAYMENT_OVERDUE", "pay_1", "sub_1", CancellationToken.None);
        // And the overdue event of the very payment that was paid since.
        await h.Processor().ProcessAsync("evt_late_overdue", "PAYMENT_OVERDUE", "pay_2", "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.PastDueSince);
        Assert.Equal(SubscriptionEventTypes.PaymentReceived, Assert.Single(await h.EventsAsync(subscription.Id)).Type);
    }

    [Theory]
    [InlineData(SubscriptionStatuses.Trialing)]
    [InlineData(SubscriptionStatuses.ReadOnly)]
    [InlineData(SubscriptionStatuses.Canceled)]
    public async Task Overdue_only_affects_a_paying_subscription(string status)
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), status, providerSubscriptionId: "sub_1");
        h.Gateway.AddPayment("pay_1", "sub_1", "cus_of_sub_1", GatewayChargeStatus.Overdue, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_o", "PAYMENT_OVERDUE", "pay_1", "sub_1", CancellationToken.None);

        Assert.Equal(status, (await h.ReloadAsync(subscription.Id)).Status);
        Assert.Empty(await h.EventsAsync(subscription.Id));
    }

    // --- SUBSCRIPTION_DELETED ---

    [Fact]
    public async Task Deleted_cancels_the_subscription_when_the_provider_confirms_it_is_gone()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        h.Gateway.Subscriptions["sub_1"] = h.Gateway.Subscriptions["sub_1"] with { Deleted = true, State = BillingSubscriptionState.Inactive };

        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Canceled, reloaded.Status);
        Assert.NotNull(reloaded.CanceledAt);
        var stored = Assert.Single(await h.EventsAsync(subscription.Id));
        Assert.Equal(SubscriptionEventTypes.Canceled, stored.Type);
        Assert.Equal("evt_d", stored.ProviderEventId);
    }

    [Fact]
    public async Task Deleted_also_cancels_when_the_provider_no_longer_knows_the_subscription()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.PastDue, providerSubscriptionId: "sub_1");
        h.Gateway.Subscriptions.Remove("sub_1");

        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.Canceled, (await h.ReloadAsync(subscription.Id)).Status);
    }

    [Fact]
    public async Task Deleted_is_ignored_while_the_provider_still_has_the_subscription_active()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");

        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(subscription.Id)).Status);
        Assert.Empty(await h.EventsAsync(subscription.Id));
    }

    [Fact]
    public async Task Deleted_after_the_tenant_cancelled_keeps_the_plan_until_the_period_ends()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_1");
        subscription.CancelAtPeriodEnd = true;
        await h.Db.SaveChangesAsync();
        h.Gateway.Subscriptions["sub_1"] = h.Gateway.Subscriptions["sub_1"] with { Deleted = true, State = BillingSubscriptionState.Inactive };

        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(subscription.Id)).Status);
    }

    [Fact]
    public async Task Deleted_on_an_unfinished_checkout_only_drops_the_provider_link_and_keeps_the_trial()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Trial Club", trial: true);
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Trialing, providerSubscriptionId: "sub_1");
        h.Gateway.Subscriptions["sub_1"] = h.Gateway.Subscriptions["sub_1"] with { Deleted = true, State = BillingSubscriptionState.Inactive };

        await h.Processor().ProcessAsync("evt_d", "SUBSCRIPTION_DELETED", null, "sub_1", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Trialing, reloaded.Status);
        Assert.Null(reloaded.ProviderSubscriptionId);
    }

    // --- tenant isolation ---

    [Fact]
    public async Task An_event_can_only_change_the_tenant_that_owns_the_provider_subscription_of_the_payment()
    {
        await using var h = await BillingHarness.CreateAsync();
        var a = await h.AddTenantAsync("Club A", taxId: "11111111000191");
        var b = await h.AddTenantAsync("Club B", taxId: "22222222000191");
        var plan = h.Plan("reservas", "profissional");
        var subA = await h.AddSubscriptionAsync(a, plan, SubscriptionStatuses.ReadOnly, providerSubscriptionId: "sub_a", providerCustomerId: "cus_a");
        var subB = await h.AddSubscriptionAsync(b, plan, SubscriptionStatuses.ReadOnly, providerSubscriptionId: "sub_b", providerCustomerId: "cus_b");
        h.Gateway.AddPayment("pay_b", "sub_b", "cus_b", GatewayChargeStatus.Paid, BillingHarness.Today);

        // The event claims tenant A's subscription, the payment is tenant B's: only B (the owner) may change.
        await h.Processor().ProcessAsync("evt_x", "PAYMENT_RECEIVED", "pay_b", "sub_a", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(subA.Id)).Status);
        Assert.Empty(await h.EventsAsync(subA.Id));
        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(subB.Id)).Status);
    }

    [Fact]
    public async Task A_payment_whose_customer_does_not_match_the_stored_customer_changes_nothing()
    {
        await using var h = await BillingHarness.CreateAsync();
        var a = await h.AddTenantAsync("Club A", taxId: "11111111000191");
        var subA = await h.AddSubscriptionAsync(
            a, h.Plan("reservas", "profissional"), SubscriptionStatuses.ReadOnly, providerSubscriptionId: "sub_a", providerCustomerId: "cus_a");
        h.Gateway.AddPayment("pay_x", "sub_a", "cus_someone_else", GatewayChargeStatus.Paid, BillingHarness.Today);

        await h.Processor().ProcessAsync("evt_x", "PAYMENT_RECEIVED", "pay_x", "sub_a", CancellationToken.None);

        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(subA.Id)).Status);
        Assert.Empty(await h.EventsAsync(subA.Id));
    }

    [Fact]
    public async Task A_provider_failure_propagates_so_the_job_is_retried()
    {
        await using var h = await BillingHarness.CreateAsync();
        h.Gateway.Failure = new PaymentGatewayException("gateway_unreachable", 0);

        await Assert.ThrowsAsync<PaymentGatewayException>(
            () => h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_1", CancellationToken.None));
    }

    private static DateTimeOffset Now(int days) => BillingHarness.Now.AddDays(days);

    private sealed class FixedIntake(BillingWebhookOutcome outcome) : IBillingWebhookIntake
    {
        public string? PresentedToken { get; private set; }

        public bool Called { get; private set; }

        public Task<BillingWebhookOutcome> AcceptAsync(string? presentedToken, string payload, CancellationToken cancellationToken)
        {
            Called = true;
            PresentedToken = presentedToken;
            return Task.FromResult(outcome);
        }
    }
}
