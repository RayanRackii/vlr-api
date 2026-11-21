using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authorization;
using Platform.Api.Modules.Billing.Services;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Modules.Payments.Gateways.Asaas;
using Platform.Api.Services.Billing;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Billing;

/// <summary>In-memory Asaas root account: customers, subscriptions and payments with call counters.</summary>
internal sealed class FakeBillingGateway : IPlatformBillingGateway
{
    private readonly Dictionary<string, string> _customersByReference = [];
    private int _counter;

    public string ProviderKey => "asaas";

    public int CustomerCreates { get; private set; }

    public int SubscriptionCreates { get; private set; }

    public int Cancels { get; private set; }

    public int GetPaymentCalls { get; private set; }

    public List<BillingSubscriptionUpdate> Updates { get; } = [];

    public BillingSubscriptionRequest? LastSubscriptionRequest { get; private set; }

    public BillingCustomerRequest? LastCustomerRequest { get; private set; }

    public Dictionary<string, BillingSubscriptionInfo> Subscriptions { get; } = [];

    public Dictionary<string, BillingPayment> Payments { get; } = [];

    /// <summary>When set, every call throws it (a provider outage).</summary>
    public Exception? Failure { get; set; }

    public BillingPayment AddPayment(
        string id,
        string subscriptionId,
        string customerId,
        GatewayChargeStatus status,
        DateOnly due,
        decimal value = 189m,
        BillingMethod method = BillingMethod.Pix)
    {
        var payment = new BillingPayment(
            id,
            status,
            value,
            due,
            status == GatewayChargeStatus.Paid ? due : null,
            $"https://invoice.test/{id}",
            subscriptionId,
            customerId,
            method);
        Payments[id] = payment;
        return payment;
    }

    public Task<string> CreateOrGetCustomerAsync(BillingCustomerRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        LastCustomerRequest = request;
        if (_customersByReference.TryGetValue(request.ExternalReference, out var existing))
        {
            return Task.FromResult(existing);
        }

        CustomerCreates++;
        var id = $"cus_{++_counter}";
        _customersByReference[request.ExternalReference] = id;
        return Task.FromResult(id);
    }

    public Task<BillingSubscriptionInfo> CreateSubscriptionAsync(
        BillingSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        LastSubscriptionRequest = request;
        var existing = Subscriptions.Values.FirstOrDefault(s =>
            s.CustomerId == request.CustomerId
            && s.ExternalReference == request.ExternalReference
            && s.State == BillingSubscriptionState.Active
            && !s.Deleted);
        if (existing is not null)
        {
            return Task.FromResult(existing);
        }

        SubscriptionCreates++;
        var id = $"sub_{++_counter}";
        var info = new BillingSubscriptionInfo(
            id,
            request.CustomerId,
            BillingSubscriptionState.Active,
            false,
            request.Value,
            request.Cycle,
            request.Method,
            request.NextDueDate,
            request.ExternalReference);
        Subscriptions[id] = info;
        AddPayment($"pay_{++_counter}", id, request.CustomerId, GatewayChargeStatus.Pending, request.NextDueDate, request.Value, request.Method);
        return Task.FromResult(info);
    }

    public Task<BillingSubscriptionInfo> UpdateSubscriptionAsync(
        string subscriptionId,
        BillingSubscriptionUpdate update,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Updates.Add(update);
        var info = Subscriptions[subscriptionId];
        info = info with
        {
            Value = update.Value ?? info.Value,
            Cycle = update.Cycle ?? info.Cycle,
            Method = update.Method ?? info.Method,
        };
        Subscriptions[subscriptionId] = info;
        if (update.Method is { } method)
        {
            foreach (var payment in Payments.Values.Where(p => p.SubscriptionId == subscriptionId).ToList())
            {
                Payments[payment.Id] = payment with { Method = method };
            }
        }

        return Task.FromResult(info);
    }

    public Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        Cancels++;
        if (Subscriptions.TryGetValue(subscriptionId, out var info))
        {
            Subscriptions[subscriptionId] = info with { Deleted = true, State = BillingSubscriptionState.Inactive };
        }

        return Task.CompletedTask;
    }

    public Task<BillingSubscriptionInfo> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Subscriptions.TryGetValue(subscriptionId, out var info)
            ? Task.FromResult(info)
            : throw new PaymentGatewayException("not_found", 404);
    }

    public Task<BillingPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        GetPaymentCalls++;
        return Payments.TryGetValue(paymentId, out var payment)
            ? Task.FromResult(payment)
            : throw new PaymentGatewayException("not_found", 404);
    }

    public Task<IReadOnlyList<BillingPayment>> ListSubscriptionPaymentsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        IReadOnlyList<BillingPayment> list = Payments.Values.Where(p => p.SubscriptionId == subscriptionId).ToList();
        return Task.FromResult(list);
    }

    public Task<BillingPixQrCode> GetPaymentPixQrCodeAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        ThrowIfFailing();
        return Task.FromResult(new BillingPixQrCode("QUJD", "00020126pix-copy-paste"));
    }

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }
}

internal sealed class RecordingWebhookScheduler : IBillingWebhookJobScheduler
{
    public List<(string EventId, string EventType, string? PaymentId, string? SubscriptionId)> Calls { get; } = [];

    public void Schedule(string eventId, string eventType, string? paymentId, string? subscriptionId) =>
        Calls.Add((eventId, eventType, paymentId, subscriptionId));
}

internal sealed class FakeActorAccessor(Guid tenantId, Guid? userId) : IRbacActorAccessor
{
    public Task<RbacActor> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Task.FromResult(new RbacActor(tenantId, userId, false));
}

/// <summary>Database, clock, fake provider and the billing services built on them.</summary>
internal sealed class BillingHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 12, 10, 12, 0, 0, TimeSpan.Zero);

    public static readonly DateOnly Today = new(2026, 12, 10);

    private readonly List<PlanCatalog> _plans;

    private BillingHarness(AppDbContext db, FakeTenantProvider provider, List<PlanCatalog> plans)
    {
        Db = db;
        TenantProvider = provider;
        _plans = plans;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public TestTimeProvider Time { get; } = new(Now);

    public FakeBillingGateway Gateway { get; } = new();

    public RecordingWebhookScheduler Scheduler { get; } = new();

    public static async Task<BillingHarness> CreateAsync()
    {
        var provider = new FakeTenantProvider { TenantId = null };
        var db = InMemoryAppDb.Create(provider);
        var plans = BillingTestData.AddCatalog(db).ToList();
        await db.SaveChangesAsync();
        return new BillingHarness(db, provider, plans);
    }

    public PlanCatalog Plan(string package, string plan) =>
        _plans.Single(p => p.PackageKey == package && p.PlanKey == plan);

    public async Task<Tenant> AddTenantAsync(
        string name,
        bool trial = false,
        string? contactEmail = "owner@club.test",
        string? taxId = null)
    {
        var tenant = new Tenant(name, taxId ?? "12345678000199");
        if (contactEmail is not null)
        {
            tenant.UpdateContactAndAddress(null, contactEmail, "+5511999998888", null, null, null, null, null, null, null);
        }

        if (trial)
        {
            tenant.ConfigureAsTrial(Now.AddDays(-10));
        }

        Db.Tenants.Add(tenant);
        await Db.SaveChangesAsync();
        return tenant;
    }

    /// <summary>A provider-billed subscription already tied to a provider customer and subscription.</summary>
    public async Task<TenantSubscription> AddSubscriptionAsync(
        Tenant tenant,
        PlanCatalog plan,
        string status,
        string billingMode = BillingModes.Provider,
        string cycle = BillingCycles.Monthly,
        string? providerSubscriptionId = null,
        string? providerCustomerId = null,
        DateTimeOffset? periodEnd = null,
        DateTimeOffset? lastPaymentAt = null)
    {
        var subscription = BillingTestData.Subscribe(Db, tenant, plan, status, billingMode);
        subscription.BillingCycle = cycle;
        subscription.CurrentPeriodEnd = periodEnd;
        subscription.LastPaymentAt = lastPaymentAt;
        if (providerSubscriptionId is not null)
        {
            subscription.Provider = "asaas";
            subscription.ProviderSubscriptionId = providerSubscriptionId;
            subscription.ProviderCustomerId = providerCustomerId ?? $"cus_of_{providerSubscriptionId}";
            Gateway.Subscriptions[providerSubscriptionId] = new BillingSubscriptionInfo(
                providerSubscriptionId,
                subscription.ProviderCustomerId,
                BillingSubscriptionState.Active,
                false,
                189m,
                cycle == BillingCycles.Annual ? BillingCycleKind.Yearly : BillingCycleKind.Monthly,
                BillingMethod.Pix,
                Today,
                tenant.Id.ToString());
        }

        await Db.SaveChangesAsync();
        return subscription;
    }

    public async Task<TenantSubscription> ReloadAsync(Guid subscriptionId) =>
        await Db.TenantSubscriptions.AsNoTracking().SingleAsync(s => s.Id == subscriptionId);

    public Task<List<SubscriptionEvent>> EventsAsync(Guid subscriptionId) =>
        Db.SubscriptionEvents.AsNoTracking().Where(e => e.SubscriptionId == subscriptionId).ToListAsync();

    public PlanUsageService Usage() => new(Db, new FakePlatformAdminChecker(), Time);

    public PlanModuleEnabler Enabler() => new(Db, new FakeTenantAccessBootstrapper());

    public TenantBillingService Billing(IPlatformBillingGateway? gateway = null, ILogger<TenantBillingService>? logger = null) =>
        new(Db, gateway ?? Gateway, Usage(), Enabler(), Time, logger ?? NullLogger<TenantBillingService>.Instance);

    public BillingWebhookProcessor Processor() =>
        new(Db, Gateway, Enabler(), Time, NullLogger<BillingWebhookProcessor>.Instance);

    public BillingWebhookIntake Intake(string? configuredToken, string environment = "Production") =>
        new(
            new AsaasBillingWebhookTokenValidator(new StaticAsaasOptions(new AsaasOptions { BillingWebhookToken = configuredToken })),
            new FakeHostEnvironment { EnvironmentName = environment },
            Db,
            Scheduler,
            NullLogger<BillingWebhookIntake>.Instance);

    public TenantBillingReadService Read() => new(Db, new PlanCatalogService(Db), Usage(), Time);

    public static string PaymentPayload(string eventId, string eventType, string paymentId, string subscriptionId, string customerId) =>
        $$$"""{"id":"{{{eventId}}}","event":"{{{eventType}}}","payment":{"id":"{{{paymentId}}}","subscription":"{{{subscriptionId}}}","customer":"{{{customerId}}}"}}""";

    public static string SubscriptionPayload(string eventId, string eventType, string subscriptionId, string customerId) =>
        $$$"""{"id":"{{{eventId}}}","event":"{{{eventType}}}","subscription":{"id":"{{{subscriptionId}}}","customer":"{{{customerId}}}"}}""";

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
