using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Append-only history of a subscription (<c>billing.subscription_events</c>). Not tenant scoped (see
/// <see cref="TenantSubscription"/>). <see cref="ProviderEventId"/> is unique and dedupes provider webhooks.
/// The payload never holds card data.
/// </summary>
public class SubscriptionEvent : Entity
{
    public const int TypeMaxLength = 32;

    public const int ProviderEventIdMaxLength = 100;

    public Guid TenantId { get; set; }

    public Guid SubscriptionId { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? ProviderEventId { get; set; }

    /// <summary>jsonb object.</summary>
    public string PayloadJson { get; set; } = "{}";
}
