using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

/// <summary>
/// Daily: a discount whose end date has passed becomes 0 percent, and the provider subscription is moved to the
/// full price first (a provider failure leaves the discount in place so the next run retries; the two never
/// diverge). Billing rows are not tenant scoped, so the job selects by explicit predicates in a platform scope.
/// </summary>
public sealed class BillingDiscountExpiryJob(
    AppDbContext dbContext,
    IPlatformBillingGateway gateway,
    TimeProvider timeProvider,
    ILogger<BillingDiscountExpiryJob> logger)
{
    /// <summary>Rows per run: the single Hangfire worker must stay short.</summary>
    public const int BatchSize = 200;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "billing-discount-expiry");
        var now = timeProvider.GetUtcNow();

        var due = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.EndedAt == null
                && s.DiscountPercent > 0
                && s.DiscountUntil != null
                && s.DiscountUntil <= now)
            .OrderBy(s => s.DiscountUntil)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var subscriptionId in due)
        {
            try
            {
                await ExpireAsync(subscriptionId, now, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad row must not stop the others; it is retried on the next run.
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(ex, "Could not end the discount of subscription {SubscriptionId}.", subscriptionId);
            }
        }

        logger.LogInformation("BillingDiscountExpiryJob finished. Due: {Count}.", due.Count);
    }

    private async Task ExpireAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var subscription = await dbContext.TenantSubscriptions
            .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
        if (subscription is null
            || subscription.EndedAt is not null
            || subscription.DiscountPercent <= 0
            || subscription.DiscountUntil is not { } until
            || until > now)
        {
            return;
        }

        var previousPercent = subscription.DiscountPercent;
        int? valueCents = null;

        if (subscription is
            {
                BillingMode: BillingModes.Provider,
                ProviderSubscriptionId: { } providerSubscriptionId,
                CancelAtPeriodEnd: false,
            }
            && subscription.Status != SubscriptionStatuses.Canceled)
        {
            // The provider charges the plan that is (or will be) in force: a scheduled downgrade is already priced.
            var planId = subscription.PendingPlanCatalogId ?? subscription.PlanCatalogId;
            var cycle = BillingCycles.IsKnown(subscription.PendingBillingCycle)
                ? subscription.PendingBillingCycle!
                : subscription.BillingCycle;
            var plan = await dbContext.PlanCatalogs.AsNoTracking()
                .FirstAsync(p => p.Id == planId, cancellationToken);

            valueCents = BillingPricing.ChargeCents(plan, cycle, discountPercent: 0);
            await gateway.UpdateSubscriptionAsync(
                providerSubscriptionId,
                new BillingSubscriptionUpdate(
                    BillingPricing.ProviderValue(valueCents.Value),
                    cycle == BillingCycles.Annual ? BillingCycleKind.Yearly : BillingCycleKind.Monthly,
                    Method: null,
                    UpdatePendingPayments: false),
                cancellationToken);
        }

        subscription.DiscountPercent = 0;
        subscription.DiscountUntil = null;
        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.PlanChanged,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "discount_expiry",
                previousDiscountPercent = previousPercent,
                endedAt = until,
                valueCents,
            }),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Daily: a provider-billed subscription the tenant asked to cancel stays in force until its paid period ends;
/// then the workspace becomes read-only (data kept). Manual rows are never touched.
/// </summary>
public sealed class BillingPeriodEndJob(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<BillingPeriodEndJob> logger)
{
    public const int BatchSize = 200;

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "billing-period-end");
        var now = timeProvider.GetUtcNow();

        var due = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.EndedAt == null
                && s.BillingMode == BillingModes.Provider
                && s.CancelAtPeriodEnd
                && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.PastDue)
                && s.CurrentPeriodEnd != null
                && s.CurrentPeriodEnd <= now)
            .OrderBy(s => s.CurrentPeriodEnd)
            .Select(s => s.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var subscriptionId in due)
        {
            try
            {
                var subscription = await dbContext.TenantSubscriptions
                    .FirstOrDefaultAsync(s => s.Id == subscriptionId, cancellationToken);
                if (subscription is null
                    || !subscription.CancelAtPeriodEnd
                    || subscription.BillingMode != BillingModes.Provider
                    || subscription.CurrentPeriodEnd is not { } end
                    || end > now
                    || subscription.Status is not (SubscriptionStatuses.Active or SubscriptionStatuses.PastDue))
                {
                    continue;
                }

                subscription.Status = SubscriptionStatuses.ReadOnly;
                subscription.PastDueSince = null;
                subscription.StampConcurrency();
                dbContext.SubscriptionEvents.Add(new SubscriptionEvent
                {
                    TenantId = subscription.TenantId,
                    SubscriptionId = subscription.Id,
                    Type = SubscriptionEventTypes.ReadOnly,
                    PayloadJson = JsonSerializer.Serialize(new { source = "cancel_at_period_end", periodEnd = end }),
                });
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                dbContext.ChangeTracker.Clear();
                logger.LogWarning(ex, "Could not close the period of subscription {SubscriptionId}.", subscriptionId);
            }
        }

        logger.LogInformation("BillingPeriodEndJob finished. Due: {Count}.", due.Count);
    }
}
