using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Services.Billing;

public enum BillingWebhookOutcome
{
    /// <summary>The token is missing or wrong (or not configured outside Development/Testing): answer 401.</summary>
    Unauthorized,

    /// <summary>A job was queued for the event.</summary>
    Accepted,

    /// <summary>Authentic but nothing to do (unknown event, unknown subscription, duplicate): answer 200.</summary>
    Ignored,
}

/// <summary>Event names of the Asaas billing webhook that Rolvix acts on.</summary>
public static class BillingWebhookEvents
{
    public const string PaymentReceived = "PAYMENT_RECEIVED";
    public const string PaymentConfirmed = "PAYMENT_CONFIRMED";
    public const string PaymentOverdue = "PAYMENT_OVERDUE";
    public const string SubscriptionDeleted = "SUBSCRIPTION_DELETED";

    public static readonly IReadOnlyList<string> Handled =
        [PaymentReceived, PaymentConfirmed, PaymentOverdue, SubscriptionDeleted];

    /// <summary>The header Asaas puts the shared token in.</summary>
    public const string TokenHeader = "asaas-access-token";
}

/// <summary>
/// First half of the billing webhook, run inside the request: authenticate, parse, resolve the tenant from the
/// stored provider ids, drop duplicates and queue the work. It never changes subscription state itself, so the
/// answer to Asaas is fast.
/// </summary>
public interface IBillingWebhookIntake
{
    Task<BillingWebhookOutcome> AcceptAsync(string? presentedToken, string payload, CancellationToken cancellationToken);
}

public interface IBillingWebhookJobScheduler
{
    void Schedule(string eventId, string eventType, string? paymentId, string? subscriptionId);
}

public sealed class HangfireBillingWebhookJobScheduler : IBillingWebhookJobScheduler
{
    public void Schedule(string eventId, string eventType, string? paymentId, string? subscriptionId)
    {
        Hangfire.BackgroundJob.Enqueue<IBillingWebhookProcessor>(
            processor => processor.ProcessAsync(eventId, eventType, paymentId, subscriptionId, CancellationToken.None));
    }
}

public sealed class BillingWebhookIntake(
    IBillingWebhookTokenValidator tokenValidator,
    IHostEnvironment environment,
    AppDbContext dbContext,
    IBillingWebhookJobScheduler scheduler,
    ILogger<BillingWebhookIntake> logger) : IBillingWebhookIntake
{
    public async Task<BillingWebhookOutcome> AcceptAsync(
        string? presentedToken,
        string payload,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(presentedToken))
        {
            return BillingWebhookOutcome.Unauthorized;
        }

        var parsed = BillingWebhookPayload.TryParse(payload);
        if (parsed is null || !BillingWebhookEvents.Handled.Contains(parsed.EventType, StringComparer.Ordinal))
        {
            return BillingWebhookOutcome.Ignored;
        }

        using var _ = PlatformScope.Begin(logger, "billing-webhook-intake");

        if (await dbContext.SubscriptionEvents.AsNoTracking()
                .AnyAsync(e => e.ProviderEventId == parsed.EventId, cancellationToken))
        {
            return BillingWebhookOutcome.Ignored;
        }

        // The tenant is whoever owns the stored provider subscription: the payload tenant data is never used.
        var subscription = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.ProviderSubscriptionId == parsed.SubscriptionId && s.EndedAt == null)
            .Select(s => new { s.BillingMode, s.ProviderCustomerId })
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null
            || subscription.BillingMode != BillingModes.Provider
            || (parsed.CustomerId is not null
                && subscription.ProviderCustomerId is not null
                && !string.Equals(parsed.CustomerId, subscription.ProviderCustomerId, StringComparison.Ordinal)))
        {
            logger.LogInformation("Billing webhook event {EventType} does not match a provider subscription; ignored.", parsed.EventType);
            return BillingWebhookOutcome.Ignored;
        }

        scheduler.Schedule(parsed.EventId, parsed.EventType, parsed.PaymentId, parsed.SubscriptionId);
        return BillingWebhookOutcome.Accepted;
    }

    /// <summary>
    /// Without a configured token the webhook cannot be authenticated, so it fails closed; Development and Testing
    /// keep the open path for local tooling and tests.
    /// </summary>
    private bool IsAuthorized(string? presentedToken)
    {
        switch (tokenValidator.Validate(presentedToken))
        {
            case BillingWebhookTokenCheck.Valid:
                return true;

            case BillingWebhookTokenCheck.NotConfigured:
                if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                {
                    logger.LogWarning(
                        "The billing webhook token is not configured; skipping billing webhook authentication ({Environment}).",
                        environment.EnvironmentName);
                    return true;
                }

                logger.LogError(
                    "The billing webhook token is not configured; rejecting the billing webhook ({Environment}).",
                    environment.EnvironmentName);
                return false;

            default:
                return false;
        }
    }
}

/// <summary>The few fields of an Asaas webhook body Rolvix reads. Everything else is ignored.</summary>
internal sealed record BillingWebhookPayload(
    string EventId,
    string EventType,
    string? PaymentId,
    string? SubscriptionId,
    string? CustomerId)
{
    public static BillingWebhookPayload? TryParse(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var eventId = Read(root, "id");
            var eventType = Read(root, "event");
            if (string.IsNullOrWhiteSpace(eventId)
                || eventId.Length > SubscriptionEvent.ProviderEventIdMaxLength
                || string.IsNullOrWhiteSpace(eventType))
            {
                return null;
            }

            string? paymentId = null;
            string? subscriptionId = null;
            string? customerId = null;
            if (root.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object)
            {
                paymentId = Read(payment, "id");
                subscriptionId = Read(payment, "subscription");
                customerId = Read(payment, "customer");
            }
            else if (root.TryGetProperty("subscription", out var subscription) && subscription.ValueKind == JsonValueKind.Object)
            {
                subscriptionId = Read(subscription, "id");
                customerId = Read(subscription, "customer");
            }

            if (string.IsNullOrWhiteSpace(subscriptionId))
            {
                return null;
            }

            if (eventType.StartsWith("PAYMENT_", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(paymentId))
            {
                return null;
            }

            return new BillingWebhookPayload(eventId, eventType, paymentId, subscriptionId, customerId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Read(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>
/// Second half of the billing webhook, run by Hangfire. Subscription state only changes from what Asaas says now
/// (the payment or subscription is fetched again) for the subscription that owns the stored provider ids, in a
/// <see cref="PlatformScope"/> with explicit tenant filters. Transitions are idempotent and monotonic: the due
/// date of the last applied payment (<see cref="TenantSubscription.LastPaymentAt"/>) and the row version make a
/// repeated, older or out-of-order event harmless. Manual rows are never touched.
/// </summary>
public interface IBillingWebhookProcessor
{
    /// <summary>Throws on a provider failure so the job is retried; a repeated run is safe.</summary>
    Task ProcessAsync(
        string eventId,
        string eventType,
        string? paymentId,
        string? subscriptionId,
        CancellationToken cancellationToken);
}

public sealed class BillingWebhookProcessor(
    AppDbContext dbContext,
    IPlatformBillingGateway gateway,
    IPlanModuleEnabler moduleEnabler,
    TimeProvider timeProvider,
    ILogger<BillingWebhookProcessor> logger) : IBillingWebhookProcessor
{
    public const string EventIdIndexName = "ux_subscription_events_provider_event_id";

    public async Task ProcessAsync(
        string eventId,
        string eventType,
        string? paymentId,
        string? subscriptionId,
        CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "billing-webhook");

        if (await dbContext.SubscriptionEvents.AsNoTracking()
                .AnyAsync(e => e.ProviderEventId == eventId, cancellationToken))
        {
            return;
        }

        switch (eventType)
        {
            case BillingWebhookEvents.PaymentReceived:
            case BillingWebhookEvents.PaymentConfirmed:
                await OnPaymentReceivedAsync(eventId, eventType, paymentId, cancellationToken);
                break;

            case BillingWebhookEvents.PaymentOverdue:
                await OnPaymentOverdueAsync(eventId, eventType, paymentId, cancellationToken);
                break;

            case BillingWebhookEvents.SubscriptionDeleted:
                await OnSubscriptionDeletedAsync(eventId, eventType, subscriptionId, cancellationToken);
                break;

            default:
                logger.LogInformation("Billing webhook event {EventType} is not handled.", eventType);
                break;
        }
    }

    private async Task OnPaymentReceivedAsync(
        string eventId,
        string eventType,
        string? paymentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(paymentId))
        {
            return;
        }

        // The webhook only says "look at this payment": what it is now comes from Asaas.
        var payment = await gateway.GetPaymentAsync(paymentId, cancellationToken);
        if (payment.Status != GatewayChargeStatus.Paid
            || payment.SubscriptionId is null
            || (payment.DueDate ?? payment.PaymentDate) is not { } dueDate)
        {
            logger.LogInformation("Billing payment {PaymentId} is not paid or not tied to a subscription; ignored.", paymentId);
            return;
        }

        var subscription = await LoadAsync(payment.SubscriptionId, payment.CustomerId, cancellationToken);
        if (subscription is null)
        {
            return;
        }

        var dueAt = BrazilTimeZone.AtLocal(dueDate, TimeOnly.MinValue);
        if (subscription.LastPaymentAt is { } last && dueAt <= last)
        {
            // Same payment reported twice (CONFIRMED then RECEIVED) or an older one: already covered.
            return;
        }

        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Id == subscription.TenantId, cancellationToken);

        var previousStatus = subscription.Status;
        var periodEnd = subscription.CurrentPeriodEnd;

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // A plan chosen at checkout starts with the first payment; a scheduled downgrade with the renewal.
        PlanCatalog? appliedPlan = null;
        IReadOnlyList<string> enabledModules = [];
        if (subscription.PendingPlanCatalogId is { } pendingId
            && (previousStatus is SubscriptionStatuses.Trialing or SubscriptionStatuses.ReadOnly or SubscriptionStatuses.Canceled
                || periodEnd is null
                || dueAt >= periodEnd))
        {
            appliedPlan = await dbContext.PlanCatalogs.FirstOrDefaultAsync(p => p.Id == pendingId, cancellationToken);
            if (appliedPlan is not null)
            {
                subscription.PlanCatalogId = appliedPlan.Id;
                if (BillingCycles.IsKnown(subscription.PendingBillingCycle))
                {
                    subscription.BillingCycle = subscription.PendingBillingCycle!;
                }

                enabledModules = await moduleEnabler.StageAsync(subscription.TenantId, appliedPlan.GetModules(), cancellationToken);
            }

            subscription.PendingPlanCatalogId = null;
            subscription.PendingBillingCycle = null;
        }

        var newEnd = subscription.BillingCycle == BillingCycles.Annual ? dueAt.AddYears(1) : dueAt.AddMonths(1);
        if (subscription.CurrentPeriodEnd is null || newEnd > subscription.CurrentPeriodEnd)
        {
            subscription.CurrentPeriodStart = dueAt;
            subscription.CurrentPeriodEnd = newEnd;
        }

        subscription.Status = SubscriptionStatuses.Active;
        subscription.PastDueSince = null;
        subscription.LastPaymentAt = dueAt;
        if (previousStatus == SubscriptionStatuses.Canceled)
        {
            subscription.CanceledAt = null;
        }

        // The subscription is the source of truth: a paying tenant leaves the trial lifecycle.
        var converted = false;
        if (tenant is not null && (tenant.IsTrial || tenant.TrialEndsAt is not null || tenant.TrialPurgeAt is not null))
        {
            tenant.ConvertFromTrial();
            converted = true;
        }

        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Type = previousStatus is SubscriptionStatuses.ReadOnly or SubscriptionStatuses.Canceled
                ? SubscriptionEventTypes.Reactivated
                : SubscriptionEventTypes.PaymentReceived,
            ProviderEventId = eventId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "asaas",
                @event = eventType,
                paymentId = payment.Id,
                dueDate = dueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                valueCents = (int)Math.Round(payment.Value * 100m, MidpointRounding.AwayFromZero),
                fromStatus = previousStatus,
                appliedPlanCatalogId = appliedPlan?.Id,
                modulesEnabled = enabledModules,
                tenantTrialConverted = converted,
            }),
        });

        if (!await TrySaveAsync(transaction, cancellationToken))
        {
            return;
        }

        await moduleEnabler.AfterCommitAsync(subscription.TenantId, enabledModules, cancellationToken);
    }

    private async Task OnPaymentOverdueAsync(
        string eventId,
        string eventType,
        string? paymentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(paymentId))
        {
            return;
        }

        var payment = await gateway.GetPaymentAsync(paymentId, cancellationToken);
        if (payment.Status != GatewayChargeStatus.Overdue
            || payment.SubscriptionId is null
            || payment.DueDate is not { } dueDate)
        {
            // Paid in the meantime (or deleted): the newer fact wins.
            return;
        }

        var subscription = await LoadAsync(payment.SubscriptionId, payment.CustomerId, cancellationToken);
        if (subscription is null)
        {
            return;
        }

        var dueAt = BrazilTimeZone.AtLocal(dueDate, TimeOnly.MinValue);
        if (subscription.LastPaymentAt is { } last && dueAt <= last)
        {
            // A payment due on or after this one was already received: this overdue notice is stale.
            return;
        }

        // Only a paying subscription falls behind; a checkout never paid (trialing), a blocked one and a
        // cancelled one keep their state.
        if (subscription.Status != SubscriptionStatuses.Active)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        subscription.Status = SubscriptionStatuses.PastDue;
        subscription.PastDueSince = dueAt < now ? dueAt : now;
        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.PaymentOverdue,
            ProviderEventId = eventId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "asaas",
                @event = eventType,
                paymentId = payment.Id,
                dueDate = dueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                valueCents = (int)Math.Round(payment.Value * 100m, MidpointRounding.AwayFromZero),
            }),
        });

        await TrySaveAsync(null, cancellationToken);
    }

    private async Task OnSubscriptionDeletedAsync(
        string eventId,
        string eventType,
        string? subscriptionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(subscriptionId))
        {
            return;
        }

        BillingSubscriptionInfo? current;
        try
        {
            current = await gateway.GetSubscriptionAsync(subscriptionId, cancellationToken);
        }
        catch (PaymentGatewayException ex) when (ex.HttpStatus == 404)
        {
            current = null;
        }

        if (current is { Deleted: false, State: BillingSubscriptionState.Active })
        {
            logger.LogInformation("Billing subscription {SubscriptionId} is still active at the provider; delete event ignored.", subscriptionId);
            return;
        }

        var subscription = await LoadAsync(subscriptionId, current?.CustomerId, cancellationToken);
        if (subscription is null || subscription.Status == SubscriptionStatuses.Canceled)
        {
            return;
        }

        // The tenant asked for this: the plan stays until the paid period ends (the period-end job acts then).
        if (subscription.CancelAtPeriodEnd)
        {
            return;
        }

        if (subscription.Status == SubscriptionStatuses.Trialing)
        {
            // An unfinished checkout was removed at the provider: the trial itself is unaffected.
            subscription.ProviderSubscriptionId = null;
            subscription.PendingPlanCatalogId = null;
            subscription.PendingBillingCycle = null;
        }
        else
        {
            subscription.Status = SubscriptionStatuses.Canceled;
            subscription.CanceledAt = timeProvider.GetUtcNow();
            subscription.PendingPlanCatalogId = null;
            subscription.PendingBillingCycle = null;
        }

        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.Canceled,
            ProviderEventId = eventId,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "asaas",
                @event = eventType,
                subscriptionId,
            }),
        });

        await TrySaveAsync(null, cancellationToken);
    }

    /// <summary>
    /// The current row of the provider subscription, only when it is provider billed and the customer matches the
    /// stored one. Tracked, with explicit predicates (billing rows are not tenant scoped).
    /// </summary>
    private async Task<TenantSubscription?> LoadAsync(
        string providerSubscriptionId,
        string? providerCustomerId,
        CancellationToken cancellationToken)
    {
        var subscription = await dbContext.TenantSubscriptions
            .Where(s => s.ProviderSubscriptionId == providerSubscriptionId && s.EndedAt == null)
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            logger.LogInformation("Billing event for an unknown provider subscription; ignored.");
            return null;
        }

        if (subscription.BillingMode != BillingModes.Provider)
        {
            return null;
        }

        if (providerCustomerId is not null
            && subscription.ProviderCustomerId is not null
            && !string.Equals(providerCustomerId, subscription.ProviderCustomerId, StringComparison.Ordinal))
        {
            logger.LogWarning("Billing event customer does not match the stored customer of subscription {SubscriptionId}; ignored.", subscription.Id);
            return null;
        }

        return subscription;
    }

    /// <summary>
    /// Saves the transition. False when the same provider event was stored meanwhile (already processed). A
    /// concurrent change of the row is not swallowed: the job fails and its retry re-evaluates the event.
    /// </summary>
    private async Task<bool> TrySaveAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return true;
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && IsDuplicateEvent(ex))
        {
            dbContext.ChangeTracker.Clear();
            logger.LogInformation("Billing webhook event was already processed.");
            return false;
        }
    }

    private static bool IsDuplicateEvent(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: EventIdIndexName,
        };
}
