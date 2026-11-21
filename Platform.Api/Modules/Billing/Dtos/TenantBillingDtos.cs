using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Billing.Dtos;

/// <summary>Resources the tenant uses now, counted the way the plan guard counts them.</summary>
public sealed record PlanUsageDto(
    int Assets,
    int RentableResources,
    int Users,
    int Units);

/// <summary>A history entry the tenant can see (payments and status changes). No card data, ever.</summary>
public sealed record BillingHistoryEventDto(
    Guid Id,
    string Type,
    DateTimeOffset CreatedAt,
    int? ValueCents,
    DateOnly? DueDate);

/// <summary>
/// The tenant's own subscription. <see cref="HasSubscription"/> is false (every other field empty) when the
/// tenant has none. <see cref="PlanLimits"/> are the limits in force: all null when the row is unlimited.
/// </summary>
public sealed record TenantBillingSubscriptionDto(
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
    DateTimeOffset? NextChargeAt,
    DateTimeOffset? TrialEndsAt,
    int DiscountPercent,
    DateTimeOffset? DiscountUntil,
    int EffectiveDiscountPercent,
    int ChargeCents,
    int MonthlyEquivalentCents,
    string? Currency,
    DateTimeOffset? PastDueSince,
    bool CancelAtPeriodEnd,
    Guid? PendingPlanCatalogId,
    string? PendingBillingCycle,
    PlanLimitsDto? PlanLimits,
    PlanUsageDto? Usage,
    IReadOnlyList<BillingHistoryEventDto> Events);

/// <summary>Active plans a tenant can move to, with the plan it is on now.</summary>
public sealed record TenantBillingPlansDto(
    Guid? CurrentPlanCatalogId,
    string? CurrentPackageKey,
    string? CurrentBillingCycle,
    IReadOnlyList<PublicPlanDto> Plans);

public sealed record CheckoutRequestDto
{
    public Guid PlanCatalogId { get; init; }

    /// <summary>monthly or annual.</summary>
    public string? BillingCycle { get; init; }

    /// <summary>pix, credit_card or boleto.</summary>
    public string? Method { get; init; }
}

public sealed record ChangePlanRequestDto
{
    public Guid PlanCatalogId { get; init; }

    /// <summary>monthly or annual.</summary>
    public string? BillingCycle { get; init; }
}

/// <summary>
/// Where the tenant pays. <see cref="InvoiceUrl"/> serves card and boleto; for Pix the QR image (base64 PNG) and
/// the copy-and-paste code are returned as well.
/// </summary>
public sealed record CheckoutResponseDto(
    string InvoiceUrl,
    string? PixQrCode,
    string? PixCopyPaste)
{
    /// <summary>The local subscription row; the idempotency claim stores it. Not part of the wire contract.</summary>
    [JsonIgnore]
    public Guid SubscriptionId { get; init; }
}
