namespace Platform.Api.Modules.Payments.Gateways;

/// <summary>
/// The platform's own billing against its tenants (Rolvix as the merchant). Every call uses the root account
/// credentials from configuration; no key travels through this contract. Provider-neutral.
/// </summary>
public interface IPlatformBillingGateway
{
    /// <summary>Stored in <c>tenant_subscriptions.provider</c>, for example <c>asaas</c>.</summary>
    string ProviderKey { get; }

    /// <summary>
    /// Returns the id of the customer carrying <see cref="BillingCustomerRequest.ExternalReference"/>, creating it
    /// only when none exists, so a retry never duplicates the customer.
    /// </summary>
    Task<string> CreateOrGetCustomerAsync(BillingCustomerRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the recurring subscription. An active subscription of the same customer and external reference is
    /// returned instead of creating a second one, so a retry never double charges.
    /// </summary>
    Task<BillingSubscriptionInfo> CreateSubscriptionAsync(
        BillingSubscriptionRequest request,
        CancellationToken cancellationToken = default);

    Task<BillingSubscriptionInfo> UpdateSubscriptionAsync(
        string subscriptionId,
        BillingSubscriptionUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>Cancels the subscription. A subscription that no longer exists counts as cancelled.</summary>
    Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<BillingSubscriptionInfo> GetSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default);

    Task<BillingPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default);

    /// <summary>Payments of the subscription, in the order the provider returns them (newest first).</summary>
    Task<IReadOnlyList<BillingPayment>> ListSubscriptionPaymentsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default);

    Task<BillingPixQrCode> GetPaymentPixQrCodeAsync(string paymentId, CancellationToken cancellationToken = default);
}

public enum BillingCycleKind
{
    Monthly,
    Yearly,
}

public enum BillingMethod
{
    Pix,
    CreditCard,
    Boleto,
}

public enum BillingSubscriptionState
{
    Active,
    Inactive,
    Expired,
    Unknown,
}

/// <param name="ExternalReference">The tenant id: deterministic, so a lookup before create makes the call retry-safe.</param>
public sealed record BillingCustomerRequest(
    string Name,
    string Email,
    string CpfCnpj,
    string ExternalReference,
    string? MobilePhone = null,
    bool NotificationDisabled = false);

/// <param name="Value">Amount of one charge in BRL, after any discount.</param>
/// <param name="NextDueDate">Due date of the first charge.</param>
public sealed record BillingSubscriptionRequest(
    string CustomerId,
    BillingCycleKind Cycle,
    BillingMethod Method,
    decimal Value,
    DateOnly NextDueDate,
    string Description,
    string ExternalReference);

/// <param name="UpdatePendingPayments">True also rewrites the charges already issued and not paid.</param>
public sealed record BillingSubscriptionUpdate(
    decimal? Value = null,
    BillingCycleKind? Cycle = null,
    BillingMethod? Method = null,
    bool UpdatePendingPayments = false);

public sealed record BillingSubscriptionInfo(
    string Id,
    string? CustomerId,
    BillingSubscriptionState State,
    bool Deleted,
    decimal Value,
    BillingCycleKind? Cycle,
    BillingMethod? Method,
    DateOnly? NextDueDate,
    string? ExternalReference);

public sealed record BillingPayment(
    string Id,
    GatewayChargeStatus Status,
    decimal Value,
    DateOnly? DueDate,
    DateOnly? PaymentDate,
    string? InvoiceUrl,
    string? SubscriptionId,
    string? CustomerId,
    BillingMethod? Method);

/// <param name="EncodedImage">Base64 PNG of the QR code.</param>
/// <param name="Payload">The copy-and-paste Pix code.</param>
public sealed record BillingPixQrCode(string EncodedImage, string Payload);

public enum BillingWebhookTokenCheck
{
    Valid,
    Invalid,

    /// <summary>No token is configured, so the webhook cannot be authenticated.</summary>
    NotConfigured,
}

/// <summary>Authenticates the provider's billing webhook by its shared token (constant-time comparison).</summary>
public interface IBillingWebhookTokenValidator
{
    BillingWebhookTokenCheck Validate(string? presentedToken);
}
