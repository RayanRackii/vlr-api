namespace Platform.Api.Modules.Billing.Dtos;

/// <summary>Limits of a plan. A null value means unlimited.</summary>
public sealed record PlanLimitsDto(
    int? Assets,
    int? RentableResources,
    int? Users,
    int? Units,
    int? WhatsappMessagesMonth);

/// <summary>One active plan of the public catalog (anonymous read).</summary>
public sealed record PublicPlanDto(
    Guid Id,
    string PackageKey,
    string PlanKey,
    string Name,
    int MonthlyPriceCents,
    int AnnualPriceCents,
    string Currency,
    IReadOnlyList<string> Modules,
    PlanLimitsDto Limits,
    int SortOrder);

public sealed record SubscriptionEventDto(
    Guid Id,
    string Type,
    DateTimeOffset CreatedAt);

/// <summary>
/// Current subscription of a tenant as the platform admin sees it. <see cref="HasSubscription"/> is false
/// (and every other field empty) for a tenant without one: such a tenant has no plan limits.
/// </summary>
public sealed record TenantSubscriptionDto(
    Guid TenantId,
    bool HasSubscription,
    Guid? SubscriptionId,
    Guid? PlanCatalogId,
    string? PackageKey,
    string? PlanKey,
    string? PlanName,
    string? BillingCycle,
    string? Status,
    string? BillingMode,
    bool LimitsUnlimited,
    DateTimeOffset? CurrentPeriodStart,
    DateTimeOffset? CurrentPeriodEnd,
    int DiscountPercent,
    DateTimeOffset? DiscountUntil,
    int EffectiveDiscountPercent,
    int MonthlyEquivalentCents,
    string? Currency,
    string? Provider,
    DateTimeOffset? PastDueSince,
    DateTimeOffset? CanceledAt,
    PlanLimitsDto? PlanLimits,
    IReadOnlyList<SubscriptionEventDto> Events);

/// <summary>
/// Admin edit of a tenant subscription. Every field is optional and an omitted (null) field is left
/// unchanged; <see cref="PlanCatalogId"/> is required only when the tenant has no subscription yet.
/// </summary>
public sealed record PutTenantSubscriptionRequestDto
{
    public Guid? PlanCatalogId { get; init; }

    /// <summary>monthly or annual.</summary>
    public string? BillingCycle { get; init; }

    /// <summary>trialing, active, past_due, read_only or canceled. Active converts a trial tenant to a paying one.</summary>
    public string? Status { get; init; }

    /// <summary>provider or manual. Only provider rows can be blocked by a read_only status.</summary>
    public string? BillingMode { get; init; }

    /// <summary>When true the plan limits are ignored for the tenant.</summary>
    public bool? LimitsUnlimited { get; init; }

    /// <summary>0 to 100. 0 clears the discount and its end date.</summary>
    public int? DiscountPercent { get; init; }

    public DateTimeOffset? DiscountUntil { get; init; }

    /// <summary>Named coupon. "fundador" = 40 percent for 12 months. Cannot be combined with an explicit discount.</summary>
    public string? Coupon { get; init; }
}

public sealed record BillingPackageSummaryDto(
    string PackageKey,
    int TrialingCount,
    int ActiveCount,
    int PastDueCount,
    int ReadOnlyCount,
    long MrrCents);

/// <summary>
/// Platform billing overview. MRR is the sum of the monthly-equivalent price after the discount in force
/// of the active and past_due subscriptions (annual price / 12), in cents of <see cref="Currency"/>.
/// </summary>
public sealed record BillingSummaryDto(
    string Currency,
    long MrrCents,
    int TrialingCount,
    int ActiveCount,
    int PastDueCount,
    int ReadOnlyCount,
    IReadOnlyList<BillingPackageSummaryDto> ByPackage);
