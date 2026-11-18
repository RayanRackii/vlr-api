namespace Platform.Api.Modules.Payments.Gateways;

/// <summary>Provider-neutral payment gateway. No provider types leak through this contract.</summary>
public interface IPaymentGateway
{
    /// <summary>Creates a merchant subaccount using the platform (root) credentials.</summary>
    Task<SubaccountResult> CreateSubaccountAsync(SubaccountRequest request, CancellationToken cancellationToken = default);

    Task RegisterWebhookAsync(
        string apiKey,
        string url,
        string token,
        IReadOnlyCollection<string> events,
        CancellationToken cancellationToken = default);

    Task<string> CreateCustomerAsync(string apiKey, CustomerRequest request, CancellationToken cancellationToken = default);

    Task<ChargeResult> CreatePixChargeAsync(string apiKey, PixChargeRequest request, CancellationToken cancellationToken = default);

    Task<GatewayCharge> GetChargeAsync(string apiKey, string externalId, CancellationToken cancellationToken = default);

    /// <summary>Returns the charge carrying this reference, or null when none exists.</summary>
    Task<GatewayCharge?> FindChargeByExternalReferenceAsync(
        string apiKey,
        string reference,
        CancellationToken cancellationToken = default);

    Task<GatewayAccountStatus> GetAccountStatusAsync(string apiKey, CancellationToken cancellationToken = default);
}

/// <summary>Webhook events Rolvix registers on every merchant account.</summary>
public static class PaymentWebhookEvents
{
    public static readonly IReadOnlyList<string> Default =
    [
        "PAYMENT_RECEIVED",
        "PAYMENT_CONFIRMED",
        "PAYMENT_OVERDUE",
        "PAYMENT_DELETED",
        "PAYMENT_REFUNDED",
    ];
}

public enum GatewayAccountStatus
{
    Pending,
    Active,
    Rejected,
}

public enum GatewayChargeStatus
{
    Pending,
    Paid,
    Overdue,
    Refunded,
    Deleted,
    Unknown,
}

/// <param name="CompanyType">Required by the provider for a CNPJ (for example MEI, LIMITED, INDIVIDUAL, ASSOCIATION).</param>
public sealed record SubaccountRequest(
    string Name,
    string Email,
    string CpfCnpj,
    string MobilePhone,
    string PostalCode,
    string Address,
    string AddressNumber,
    string Province,
    decimal IncomeValue,
    string? CompanyType = null,
    string? Complement = null,
    DateOnly? BirthDate = null);

/// <summary>The <see cref="ApiKey"/> is a secret: store it only through the payment secret protector.</summary>
public sealed record SubaccountResult(string ExternalAccountId, string WalletId, string ApiKey, string? OnboardingUrl)
{
    // The compiler-generated ToString would print ApiKey; keep the secret out of any log of this record.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ExternalAccountId = ").Append(ExternalAccountId)
            .Append(", WalletId = ").Append(WalletId)
            .Append(", ApiKey = ***")
            .Append(", OnboardingUrl = ").Append(OnboardingUrl);
        return true;
    }
}

public sealed record CustomerRequest(string Name, string Email, string Cpf, string? Phone = null);

/// <summary>A share of the charge sent to another wallet (the platform fee). Percent is 0-100.</summary>
public sealed record ChargeSplit(string WalletId, decimal Percent);

public sealed record PixChargeRequest(
    string CustomerId,
    decimal Amount,
    DateTimeOffset DueAt,
    string Description,
    string ExternalReference,
    ChargeSplit? Split = null);

public sealed record ChargeResult(
    string ExternalId,
    string PixPayload,
    string PixQrImageBase64,
    string? InvoiceUrl,
    DateTimeOffset DueAt);

public sealed record GatewayCharge(
    string ExternalId,
    GatewayChargeStatus Status,
    decimal Amount,
    string? ExternalReference,
    string? InvoiceUrl,
    DateOnly? PaymentDate);
