using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Services.Billing;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Billing.Services;

/// <summary>The tenant staff read side of billing: own subscription (with usage) and the plans to move to.</summary>
public interface ITenantBillingReadService
{
    Task<TenantBillingSubscriptionDto> GetSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<TenantBillingPlansDto> GetPlansAsync(Guid tenantId, CancellationToken cancellationToken);
}

/// <summary>Every query carries the explicit tenant id: the billing tables are not tenant scoped.</summary>
public sealed class TenantBillingReadService(
    AppDbContext dbContext,
    IPlanCatalogService catalog,
    IPlanUsage planUsage,
    TimeProvider timeProvider) : ITenantBillingReadService
{
    private const int EventListSize = 30;

    public async Task<TenantBillingSubscriptionDto> GetSubscriptionAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var subscription = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .Where(s => s.TenantId == tenantId && s.EndedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (subscription is null)
        {
            return new TenantBillingSubscriptionDto(
                false, null, null, null, null, null, null, null, null, false,
                null, null, null, null, 0, null, 0, 0, 0, null, null, false, null, null, null, null, []);
        }

        var now = timeProvider.GetUtcNow();
        var plan = await dbContext.PlanCatalogs.AsNoTracking()
            .FirstAsync(p => p.Id == subscription.PlanCatalogId, cancellationToken);
        var trialEndsAt = await dbContext.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.TrialEndsAt)
            .FirstOrDefaultAsync(cancellationToken);
        var usage = await planUsage.GetAsync(tenantId, cancellationToken);

        var effective = subscription.EffectiveDiscountPercent(now);
        var limits = subscription.LimitsUnlimited ? PlanLimitValues.Unlimited : plan.GetLimits();
        var paying = subscription.BillingMode == BillingModes.Provider
            && (subscription.Status == SubscriptionStatuses.Active || subscription.Status == SubscriptionStatuses.PastDue)
            && !subscription.CancelAtPeriodEnd;

        var events = (await dbContext.SubscriptionEvents
                .AsNoTracking()
                .Where(e => e.SubscriptionId == subscription.Id && e.TenantId == tenantId)
                .Where(e => !e.Type.StartsWith(SubscriptionEventTypes.DunningPrefix))
                .OrderByDescending(e => e.CreatedAt)
                .Take(EventListSize)
                .ToListAsync(cancellationToken))
            .Select(ToHistory)
            .ToList();

        return new TenantBillingSubscriptionDto(
            true,
            subscription.Id,
            plan.Id,
            plan.PackageKey,
            plan.PlanKey,
            plan.Name,
            subscription.BillingCycle,
            subscription.Status,
            subscription.BillingMode,
            subscription.LimitsUnlimited,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            paying ? subscription.CurrentPeriodEnd : null,
            trialEndsAt,
            subscription.DiscountPercent,
            subscription.DiscountUntil,
            effective,
            BillingPricing.ChargeCents(plan, subscription.BillingCycle, effective),
            SubscriptionPricing.Round(SubscriptionPricing.MonthlyEquivalentCents(
                subscription.BillingCycle, plan.MonthlyPriceCents, plan.AnnualPriceCents, effective)),
            plan.Currency,
            subscription.PastDueSince,
            subscription.CancelAtPeriodEnd,
            subscription.PendingPlanCatalogId,
            subscription.PendingBillingCycle,
            PlanCatalogService.ToDto(limits),
            usage.ToDto(),
            events);
    }

    public async Task<TenantBillingPlansDto> GetPlansAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var plans = await catalog.ListActiveAsync(cancellationToken);
        var current = await (
                from subscription in dbContext.TenantSubscriptions.AsNoTracking()
                join plan in dbContext.PlanCatalogs.AsNoTracking()
                    on subscription.PlanCatalogId equals plan.Id
                where subscription.TenantId == tenantId && subscription.EndedAt == null
                orderby subscription.CreatedAt descending
                select new { plan.Id, plan.PackageKey, subscription.BillingCycle })
            .FirstOrDefaultAsync(cancellationToken);

        return new TenantBillingPlansDto(current?.Id, current?.PackageKey, current?.BillingCycle, plans);
    }

    /// <summary>Only the value and due date of a payment are surfaced from the stored payload.</summary>
    private static BillingHistoryEventDto ToHistory(SubscriptionEvent e)
    {
        int? valueCents = null;
        DateOnly? dueDate = null;
        try
        {
            using var document = JsonDocument.Parse(e.PayloadJson);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("valueCents", out var value) && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var cents))
                {
                    valueCents = cents;
                }

                if (root.TryGetProperty("dueDate", out var due) && due.ValueKind == JsonValueKind.String
                    && DateOnly.TryParseExact(due.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                {
                    dueDate = date;
                }
            }
        }
        catch (JsonException)
        {
            // A payload that does not parse only loses its extras.
        }

        return new BillingHistoryEventDto(e.Id, e.Type, e.CreatedAt, valueCents, dueDate);
    }
}
