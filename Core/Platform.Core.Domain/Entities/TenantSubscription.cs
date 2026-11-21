using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// The current (or a past) subscription of a tenant (<c>billing.tenant_subscriptions</c>). At most one
/// row per tenant has <see cref="EndedAt"/> null. Deliberately not <see cref="ITenantScoped"/>: it is
/// platform billing data, read by the platform admin across tenants and by tenant code only through
/// an explicit <see cref="TenantId"/> predicate (<c>IPlanLimits</c>, <c>PlanGuard</c>).
/// </summary>
public class TenantSubscription : Entity
{
    public const int CodeMaxLength = 16;

    public const int ProviderMaxLength = 32;

    public const int ProviderIdMaxLength = 100;

    public Guid TenantId { get; set; }

    public Guid PlanCatalogId { get; set; }

    public string BillingCycle { get; set; } = BillingCycles.Monthly;

    public string Status { get; set; } = SubscriptionStatuses.Trialing;

    /// <summary>
    /// <see cref="BillingModes.Provider"/> or <see cref="BillingModes.Manual"/>. Only provider rows can
    /// be made read-only by their status; backfilled and manually billed rows are never blocked.
    /// </summary>
    public string BillingMode { get; set; } = BillingModes.Provider;

    /// <summary>When true the plan limits are ignored for this tenant (backfilled and manual rows).</summary>
    public bool LimitsUnlimited { get; set; }

    public DateTimeOffset? CurrentPeriodStart { get; set; }

    public DateTimeOffset? CurrentPeriodEnd { get; set; }

    /// <summary>0 to 100.</summary>
    public int DiscountPercent { get; set; }

    public DateTimeOffset? DiscountUntil { get; set; }

    public string? Provider { get; set; }

    public string? ProviderCustomerId { get; set; }

    public string? ProviderSubscriptionId { get; set; }

    public DateTimeOffset? PastDueSince { get; set; }

    public DateTimeOffset? CanceledAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public PlanCatalog? PlanCatalog { get; set; }

    /// <summary>Discount in force at <paramref name="utcNow"/> (0 once <see cref="DiscountUntil"/> passed).</summary>
    public int EffectiveDiscountPercent(DateTimeOffset utcNow) =>
        DiscountUntil is { } until && until <= utcNow ? 0 : Math.Clamp(DiscountPercent, 0, 100);

    public void StampConcurrency()
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
    }
}
