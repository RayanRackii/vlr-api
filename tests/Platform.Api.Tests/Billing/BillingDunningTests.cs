using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Jobs;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Notifications;
using Platform.Api.Services.Billing;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Tests.Billing;

public sealed class BillingDunningTests
{
    private static DateOnly Today => BillingHarness.Today;

    private static DateTimeOffset DueAt(DateOnly date) => BrazilTimeZone.AtLocal(date, TimeOnly.MinValue);

    private sealed class RecordingEmailSender : IBillingEmailSender
    {
        public List<(string To, string Subject, string Text, string Html)> Sent { get; } = [];

        public ValueTask SendAsync(string recipientEmail, string subject, EmailContent content, CancellationToken cancellationToken)
        {
            Sent.Add((recipientEmail, subject, content.Text, content.Html));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Setup(BillingHarness h, bool? emailsEnabled, string? language = null)
    {
        public RecordingEmailSender Sender { get; } = new();

        public FakePermissionResolver Grants { get; } = new();

        public BillingDunningJob Job()
        {
            var settings = new Dictionary<string, string?> { ["App:FrontendBaseUrl"] = "https://app.rolvix.test" };
            if (emailsEnabled is { } enabled)
            {
                settings[BillingDunningJob.EmailsEnabledKey] = enabled ? "true" : "false";
            }

            if (language is not null)
            {
                settings[BillingDunningMailer.LanguageKey] = language;
            }

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var mailer = new BillingDunningMailer(
                new StaffRecipientResolver(h.Db, Grants),
                Sender,
                configuration,
                new FakeHostEnvironment());
            return new BillingDunningJob(h.Db, mailer, configuration, h.Time, NullLogger<BillingDunningJob>.Instance);
        }

        public Task RunAsync() => Job().ExecuteAsync(CancellationToken.None);

        public async Task<User> AddBillingAdminAsync(Tenant tenant, string email, string name = "Admin", bool granted = true, bool active = true)
        {
            var user = new User(tenant.Id, Guid.NewGuid().ToString("N"), name, email);
            if (!active)
            {
                user.Deactivate();
            }

            h.Db.Users.Add(user);
            await h.Db.SaveChangesAsync();
            if (granted)
            {
                Grants.Grant(user.Id, Permissions.Core.SettingsBilling);
            }

            return user;
        }
    }

    private static async Task<List<string>> DunningEventTypesAsync(BillingHarness h, Guid subscriptionId) =>
        (await h.EventsAsync(subscriptionId))
            .Where(e => e.Type.StartsWith(SubscriptionEventTypes.DunningPrefix, StringComparison.Ordinal))
            .Select(e => e.Type)
            .ToList();

    // --- state transitions (always on) ---

    [Fact]
    public async Task A_past_due_provider_subscription_becomes_read_only_on_the_seventh_civil_day_in_brazil_time()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: null);
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.PastDue, providerSubscriptionId: "sub_a");
        // Unpaid charge due on civil day 2026-12-03 (Brazil): the seventh day after it starts on 2026-12-10 00:00 BRT.
        subscription.PastDueSince = DueAt(new DateOnly(2026, 12, 3));
        await h.Db.SaveChangesAsync();

        // 2026-12-09 23:59 in Brazil (02:59Z of the 10th): still day 6 there, although it is already the 10th in UTC.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 12, 10, 2, 59, 0, TimeSpan.Zero));
        await setup.RunAsync();
        Assert.Equal(SubscriptionStatuses.PastDue, (await h.ReloadAsync(subscription.Id)).Status);

        // 2026-12-10 00:00 in Brazil.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 12, 10, 3, 0, 0, TimeSpan.Zero));
        await setup.RunAsync();
        await setup.RunAsync();

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.ReadOnly, reloaded.Status);
        Assert.NotNull(reloaded.PastDueSince);
        var events = await h.EventsAsync(subscription.Id);
        Assert.Equal(SubscriptionEventTypes.ReadOnly, Assert.Single(events).Type);
        Assert.Empty(setup.Sender.Sent);
    }

    [Fact]
    public async Task Manual_rows_and_rows_without_a_provider_subscription_are_never_touched()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var plan = h.Plan("reservas", "profissional");

        var manualTenant = await h.AddTenantAsync("Manual", taxId: "55555555000191");
        var manual = await h.AddSubscriptionAsync(manualTenant, plan, SubscriptionStatuses.PastDue, BillingModes.Manual, providerSubscriptionId: "sub_m");
        manual.PastDueSince = BillingHarness.Now.AddDays(-30);

        var noProviderTenant = await h.AddTenantAsync("No provider", taxId: "66666666000191");
        var noProvider = await h.AddSubscriptionAsync(noProviderTenant, plan, SubscriptionStatuses.PastDue);
        noProvider.PastDueSince = BillingHarness.Now.AddDays(-30);

        var manualActiveTenant = await h.AddTenantAsync("Manual active", taxId: "77777777000191");
        var manualActive = await h.AddSubscriptionAsync(
            manualActiveTenant, plan, SubscriptionStatuses.Active, BillingModes.Manual, providerSubscriptionId: "sub_ma", periodEnd: DueAt(Today.AddDays(3)));
        await h.Db.SaveChangesAsync();

        await setup.RunAsync();

        Assert.Equal(SubscriptionStatuses.PastDue, (await h.ReloadAsync(manual.Id)).Status);
        Assert.Equal(SubscriptionStatuses.PastDue, (await h.ReloadAsync(noProvider.Id)).Status);
        Assert.Equal(SubscriptionStatuses.Active, (await h.ReloadAsync(manualActive.Id)).Status);
        Assert.Empty(await h.EventsAsync(manual.Id));
        Assert.Empty(await h.EventsAsync(noProvider.Id));
        Assert.Empty(await h.EventsAsync(manualActive.Id));
        Assert.Empty(setup.Sender.Sent);
    }

    [Fact]
    public async Task A_payment_reactivates_a_subscription_the_dunning_job_made_read_only()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: false);
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.PastDue, providerSubscriptionId: "sub_a",
            lastPaymentAt: DueAt(Today.AddDays(-40)));
        subscription.PastDueSince = DueAt(Today.AddDays(-8));
        await h.Db.SaveChangesAsync();

        await setup.RunAsync();
        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(subscription.Id)).Status);

        h.Gateway.AddPayment("pay_1", "sub_a", "cus_of_sub_a", GatewayChargeStatus.Paid, Today);
        await h.Processor().ProcessAsync("evt_1", "PAYMENT_RECEIVED", "pay_1", "sub_a", CancellationToken.None);

        var reloaded = await h.ReloadAsync(subscription.Id);
        Assert.Equal(SubscriptionStatuses.Active, reloaded.Status);
        Assert.Null(reloaded.PastDueSince);
        Assert.Contains(await h.EventsAsync(subscription.Id), e => e.Type == SubscriptionEventTypes.Reactivated);
    }

    // --- e-mails ---

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task No_e_mail_is_scheduled_unless_the_flag_is_on_but_the_transition_still_runs(bool? flag)
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, flag);
        var plan = h.Plan("reservas", "profissional");

        var upcomingTenant = await h.AddTenantAsync("Upcoming", taxId: "55555555000191");
        var upcoming = await h.AddSubscriptionAsync(
            upcomingTenant, plan, SubscriptionStatuses.Active, providerSubscriptionId: "sub_u", periodEnd: DueAt(Today.AddDays(3)));
        var overdueTenant = await h.AddTenantAsync("Overdue", taxId: "66666666000191");
        var overdue = await h.AddSubscriptionAsync(overdueTenant, plan, SubscriptionStatuses.PastDue, providerSubscriptionId: "sub_o");
        overdue.PastDueSince = DueAt(Today.AddDays(-7));
        await h.Db.SaveChangesAsync();

        await setup.RunAsync();

        Assert.Empty(setup.Sender.Sent);
        Assert.Empty(await DunningEventTypesAsync(h, upcoming.Id));
        Assert.Empty(await DunningEventTypesAsync(h, overdue.Id));
        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(overdue.Id)).Status);
    }

    [Fact]
    public async Task The_billing_calendar_sends_each_notice_once_to_the_admins_and_the_company_contact()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club Alfa");
        await setup.AddBillingAdminAsync(tenant, "admin@club.test", "Ana");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(Today.AddDays(3)));

        // D-3: upcoming charge. A rerun the same day sends nothing more.
        await setup.RunAsync();
        await setup.RunAsync();
        Assert.Equal(2, setup.Sender.Sent.Count);
        Assert.All(setup.Sender.Sent, mail => Assert.Contains("vence em 3 dias", mail.Subject));
        Assert.Equal(
            new[] { "admin@club.test", "owner@club.test" },
            setup.Sender.Sent.Select(m => m.To).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        var first = setup.Sender.Sent.Single(m => m.To == "admin@club.test");
        Assert.Contains("https://app.rolvix.test/configuracoes/plano", first.Text);
        Assert.Contains("Club Alfa", first.Text);
        Assert.Contains("13/12/2026", first.Text);
        Assert.Contains("Ana", first.Text);

        // D0: due today.
        h.Time.Advance(TimeSpan.FromDays(3));
        await setup.RunAsync();
        Assert.Equal(4, setup.Sender.Sent.Count);
        Assert.Contains("vence hoje", setup.Sender.Sent[^1].Subject);

        // The provider reports it overdue: D+1, nothing at D+2, D+5, then read-only at D+7.
        subscription.Status = SubscriptionStatuses.PastDue;
        subscription.PastDueSince = DueAt(Today.AddDays(3));
        await h.Db.SaveChangesAsync();

        h.Time.Advance(TimeSpan.FromDays(1));
        await setup.RunAsync();
        Assert.Equal(6, setup.Sender.Sent.Count);
        Assert.Equal(SubscriptionStatuses.PastDue, (await h.ReloadAsync(subscription.Id)).Status);

        h.Time.Advance(TimeSpan.FromDays(1));
        await setup.RunAsync();
        Assert.Equal(6, setup.Sender.Sent.Count);

        h.Time.Advance(TimeSpan.FromDays(3));
        await setup.RunAsync();
        Assert.Equal(8, setup.Sender.Sent.Count);
        Assert.Contains("Último aviso", setup.Sender.Sent[^1].Subject);

        h.Time.Advance(TimeSpan.FromDays(2));
        await setup.RunAsync();
        await setup.RunAsync();
        Assert.Equal(10, setup.Sender.Sent.Count);
        Assert.Contains("somente leitura", setup.Sender.Sent[^1].Subject);
        Assert.Equal(SubscriptionStatuses.ReadOnly, (await h.ReloadAsync(subscription.Id)).Status);

        Assert.Equal(
            new[] { "dunning_upcoming", "dunning_due", "dunning_overdue_1", "dunning_overdue_5", "dunning_read_only" }
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray(),
            (await DunningEventTypesAsync(h, subscription.Id)).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task The_day_is_a_civil_date_in_brazil_not_in_utc()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(new DateOnly(2026, 12, 13)));

        // 2026-12-09 23:59 in Brazil is already the 10th in UTC, yet the charge is 4 days away there.
        h.Time.SetUtcNow(new DateTimeOffset(2026, 12, 10, 2, 59, 0, TimeSpan.Zero));
        await setup.RunAsync();
        Assert.Empty(setup.Sender.Sent);

        h.Time.SetUtcNow(new DateTimeOffset(2026, 12, 10, 3, 0, 0, TimeSpan.Zero));
        await setup.RunAsync();
        var mail = Assert.Single(setup.Sender.Sent);
        Assert.Equal("owner@club.test", mail.To);
    }

    [Fact]
    public async Task A_notice_already_recorded_by_another_run_is_not_sent_again()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club");
        var due = Today.AddDays(3);
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a", periodEnd: DueAt(due));
        h.Db.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = tenant.Id,
            SubscriptionId = subscription.Id,
            Type = DunningKind.Upcoming.EventType,
            ProviderEventId = BillingDunningSchedule.DedupeKey(subscription.Id, DunningKind.Upcoming, due),
        });
        await h.Db.SaveChangesAsync();

        await setup.RunAsync();

        Assert.Empty(setup.Sender.Sent);

        // The next period is a different notice: a renewal one month later is reminded again.
        subscription.CurrentPeriodEnd = DueAt(due.AddMonths(1));
        await h.Db.SaveChangesAsync();
        h.Time.Advance(TimeSpan.FromDays(31));
        await setup.RunAsync();
        Assert.Single(setup.Sender.Sent);
    }

    [Fact]
    public async Task Nothing_is_recorded_without_a_recipient_so_a_later_run_can_still_notify()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club", contactEmail: null);
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(Today.AddDays(3)));

        await setup.RunAsync();
        Assert.Empty(setup.Sender.Sent);
        Assert.Empty(await DunningEventTypesAsync(h, subscription.Id));

        await setup.AddBillingAdminAsync(tenant, "admin@club.test");
        await setup.RunAsync();
        Assert.Equal("admin@club.test", Assert.Single(setup.Sender.Sent).To);
    }

    [Fact]
    public async Task Recipients_are_the_tenant_billing_admins_and_the_contact_without_duplicates()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club");
        var other = await h.AddTenantAsync("Other", taxId: "66666666000191", contactEmail: "other@other.test");
        await setup.AddBillingAdminAsync(tenant, "admin@club.test", "Ana");
        await setup.AddBillingAdminAsync(tenant, "Owner@Club.test", "Owner as admin");
        await setup.AddBillingAdminAsync(tenant, "nobilling@club.test", "No billing", granted: false);
        await setup.AddBillingAdminAsync(tenant, "gone@club.test", "Gone", active: false);
        await setup.AddBillingAdminAsync(other, "admin@other.test", "Foreign");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(Today));

        await setup.RunAsync();

        Assert.Equal(
            new[] { "admin@club.test", "owner@club.test" },
            setup.Sender.Sent.Select(m => m.To.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Trial_end_notices_go_out_at_five_days_one_day_and_the_last_day_once_each()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var plan = h.Plan("reservas", "profissional");

        // Trial ends on the civil day Today+5, in the afternoon.
        var endsAt = BrazilTimeZone.AtLocal(Today.AddDays(5), new TimeOnly(15, 0));
        var tenant = new Tenant("Trial Club", "55555555000191");
        tenant.UpdateContactAndAddress(null, "owner@trial.test", "+5511999998888", null, null, null, null, null, null, null);
        tenant.ConfigureAsTrial(endsAt.AddDays(-TrialLimits.TrialDays));
        h.Db.Tenants.Add(tenant);
        await h.Db.SaveChangesAsync();
        var trial = await h.AddSubscriptionAsync(tenant, plan, SubscriptionStatuses.Trialing, periodEnd: endsAt);

        // A trial that already started a checkout is not nagged; neither is a paying tenant.
        var checkoutTenant = new Tenant("Checkout Club", "66666666000191");
        checkoutTenant.UpdateContactAndAddress(null, "owner@checkout.test", "+5511999998888", null, null, null, null, null, null, null);
        checkoutTenant.ConfigureAsTrial(endsAt.AddDays(-TrialLimits.TrialDays));
        h.Db.Tenants.Add(checkoutTenant);
        await h.Db.SaveChangesAsync();
        await h.AddSubscriptionAsync(checkoutTenant, plan, SubscriptionStatuses.Trialing, providerSubscriptionId: "sub_c", periodEnd: endsAt);

        await setup.RunAsync();
        await setup.RunAsync();
        var five = Assert.Single(setup.Sender.Sent);
        Assert.Equal("owner@trial.test", five.To);
        Assert.Contains("5 dias", five.Subject);
        Assert.Contains("https://app.rolvix.test/configuracoes/plano", five.Text);
        Assert.Contains("Assine para continuar", five.Text);

        h.Time.Advance(TimeSpan.FromDays(3));
        await setup.RunAsync();
        Assert.Single(setup.Sender.Sent);

        h.Time.Advance(TimeSpan.FromDays(1));
        await setup.RunAsync();
        Assert.Equal(2, setup.Sender.Sent.Count);
        Assert.Contains("amanhã", setup.Sender.Sent[^1].Subject);

        h.Time.Advance(TimeSpan.FromDays(1));
        await setup.RunAsync();
        await setup.RunAsync();
        Assert.Equal(3, setup.Sender.Sent.Count);
        Assert.Contains("hoje", setup.Sender.Sent[^1].Subject);

        Assert.Equal(3, (await DunningEventTypesAsync(h, trial.Id)).Count);
        // A trial notice never changes the status: the trial lifecycle owns that.
        Assert.Equal(SubscriptionStatuses.Trialing, (await h.ReloadAsync(trial.Id)).Status);
    }

    [Fact]
    public async Task The_bookkeeping_events_are_not_part_of_the_history_tenants_see()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true);
        var tenant = await h.AddTenantAsync("Club");
        var subscription = await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(Today));

        await setup.RunAsync();
        Assert.Single(await DunningEventTypesAsync(h, subscription.Id));

        var read = await h.Read().GetSubscriptionAsync(tenant.Id, CancellationToken.None);
        Assert.Empty(read.Events);
    }

    [Fact]
    public void Every_kind_has_copy_in_all_languages_and_escapes_the_tenant_name()
    {
        var model = new BillingEmailModel("<b>Club & Co</b>", new DateOnly(2026, 12, 13), "https://app.rolvix.test/configuracoes/plano");
        var subjects = new HashSet<string>();

        foreach (var kind in DunningKind.All)
        {
            foreach (var language in new[] { "pt-BR", "en", "es" })
            {
                var email = BillingDunningEmails.Render(kind, language, model, "Ana");
                Assert.False(string.IsNullOrWhiteSpace(email.Subject));
                Assert.DoesNotContain("{tenant}", email.Content.Html);
                Assert.DoesNotContain("{date}", email.Content.Html);
                Assert.DoesNotContain("<b>Club", email.Content.Html);
                Assert.Contains("&lt;b&gt;Club", email.Content.Html);
                Assert.Contains("configuracoes/plano", email.Content.Text);
                Assert.True(subjects.Add($"{kind.Key}|{language}|{email.Subject}"));
            }
        }

        Assert.Equal("en", BillingDunningEmails.NormalizeLanguage("en-US"));
        Assert.Equal("es", BillingDunningEmails.NormalizeLanguage("ES"));
        Assert.Equal("pt-BR", BillingDunningEmails.NormalizeLanguage("fr"));
        Assert.Equal("pt-BR", BillingDunningEmails.NormalizeLanguage(null));
        Assert.Contains("2026-12-13", BillingDunningEmails.Render(DunningKind.Due, "en", model, null).Content.Text);
    }

    [Fact]
    public async Task The_configured_language_is_used()
    {
        await using var h = await BillingHarness.CreateAsync();
        var setup = new Setup(h, emailsEnabled: true, language: "en");
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Active, providerSubscriptionId: "sub_a",
            periodEnd: DueAt(Today));

        await setup.RunAsync();

        Assert.Contains("due today", Assert.Single(setup.Sender.Sent).Subject);
    }

    // --- PlanGuard: canceled provider rows ---

    private static PlanGuard Guard(BillingHarness h, Tenant tenant)
    {
        h.TenantProvider.TenantId = tenant.Id;
        return new PlanGuard(h.Db, h.TenantProvider, new FakePlatformAdminChecker(), h.Time);
    }

    [Fact]
    public async Task A_canceled_provider_subscription_is_read_only_once_its_period_has_ended()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Canceled, providerSubscriptionId: "sub_a",
            periodEnd: BillingHarness.Now.AddDays(-1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard(h, tenant).EnsureWritableAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard(h, tenant).EnsureCanCreateAssetsAsync(1, 0, CancellationToken.None));
    }

    [Fact]
    public async Task A_canceled_provider_subscription_without_a_period_end_is_read_only()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Canceled, providerSubscriptionId: "sub_a");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard(h, tenant).EnsureWritableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_canceled_provider_subscription_stays_writable_until_its_paid_period_ends()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Canceled, providerSubscriptionId: "sub_a",
            periodEnd: BillingHarness.Now.AddDays(5));

        await Guard(h, tenant).EnsureWritableAsync(CancellationToken.None);

        h.Time.Advance(TimeSpan.FromDays(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Guard(h, tenant).EnsureWritableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_canceled_manual_row_is_never_read_only()
    {
        await using var h = await BillingHarness.CreateAsync();
        var tenant = await h.AddTenantAsync("Club");
        await h.AddSubscriptionAsync(
            tenant, h.Plan("reservas", "profissional"), SubscriptionStatuses.Canceled, BillingModes.Manual,
            periodEnd: BillingHarness.Now.AddDays(-30));

        await Guard(h, tenant).EnsureWritableAsync(CancellationToken.None);
    }
}
