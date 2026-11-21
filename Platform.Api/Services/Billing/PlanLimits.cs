using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Services.Billing;

/// <summary>
/// The limits in force for a tenant, read from the plan of its current subscription (a trial included).
/// A tenant without a subscription, or whose subscription has <c>limits_unlimited</c>, has no limits.
/// </summary>
public sealed record EffectivePlanLimits(
    bool HasSubscription,
    string? PackageKey,
    string? PlanKey,
    string? Status,
    string? BillingMode,
    bool IsUnlimited,
    PlanLimitValues Limits)
{
    /// <summary>Safe default for the rollout window: no subscription, no limits.</summary>
    public static readonly EffectivePlanLimits None =
        new(false, null, null, null, null, true, PlanLimitValues.Unlimited);
}

public interface IPlanLimits
{
    /// <summary>
    /// Effective limits of <paramref name="tenantId"/>. Always queries by the explicit tenant id: the billing
    /// tables are not tenant scoped.
    /// </summary>
    Task<EffectivePlanLimits> GetAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class PlanLimitsService(AppDbContext dbContext) : IPlanLimits
{
    public async Task<EffectivePlanLimits> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var state = await PlanStateReader.LoadAsync(dbContext, tenantId, cancellationToken);
        return state is null ? EffectivePlanLimits.None : state.ToEffectiveLimits();
    }
}

/// <summary>Current subscription of a tenant joined with the limits of its plan.</summary>
internal sealed record PlanState(
    Guid SubscriptionId,
    string PackageKey,
    string PlanKey,
    string Status,
    string BillingMode,
    bool LimitsUnlimited,
    PlanLimitValues PlanLimits)
{
    /// <summary>Limits to enforce: none when the row is flagged unlimited.</summary>
    public PlanLimitValues EnforcedLimits => LimitsUnlimited ? PlanLimitValues.Unlimited : PlanLimits;

    public EffectivePlanLimits ToEffectiveLimits() =>
        new(true, PackageKey, PlanKey, Status, BillingMode, LimitsUnlimited, EnforcedLimits);
}

internal static class PlanStateReader
{
    public static async Task<PlanState?> LoadAsync(
        AppDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var row = await (
                from subscription in dbContext.TenantSubscriptions.AsNoTracking()
                join plan in dbContext.PlanCatalogs.AsNoTracking()
                    on subscription.PlanCatalogId equals plan.Id
                where subscription.TenantId == tenantId && subscription.EndedAt == null
                orderby subscription.CreatedAt descending
                select new
                {
                    subscription.Id,
                    plan.PackageKey,
                    plan.PlanKey,
                    subscription.Status,
                    subscription.BillingMode,
                    subscription.LimitsUnlimited,
                    plan.LimitsJson,
                })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new PlanState(
                row.Id,
                row.PackageKey,
                row.PlanKey,
                row.Status,
                row.BillingMode,
                row.LimitsUnlimited,
                PlanLimitValues.Parse(row.LimitsJson));
    }
}
