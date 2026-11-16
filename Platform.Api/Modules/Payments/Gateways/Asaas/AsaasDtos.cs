using System.Text.Json.Serialization;

namespace Platform.Api.Modules.Payments.Gateways.Asaas;

// Asaas v3 wire shapes. Internal: nothing outside the Asaas adapter sees these types.

internal sealed record AsaasCreateAccountBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("cpfCnpj")] string CpfCnpj,
    [property: JsonPropertyName("mobilePhone")] string MobilePhone,
    [property: JsonPropertyName("postalCode")] string PostalCode,
    [property: JsonPropertyName("address")] string Address,
    [property: JsonPropertyName("addressNumber")] string AddressNumber,
    [property: JsonPropertyName("province")] string Province,
    [property: JsonPropertyName("incomeValue")] decimal IncomeValue,
    [property: JsonPropertyName("companyType")] string? CompanyType,
    [property: JsonPropertyName("complement")] string? Complement,
    [property: JsonPropertyName("birthDate")] string? BirthDate);

internal sealed record AsaasAccountResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("apiKey")] string? ApiKey,
    [property: JsonPropertyName("walletId")] string? WalletId,
    [property: JsonPropertyName("onboardingUrl")] string? OnboardingUrl);

internal sealed record AsaasWebhookBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("interrupted")] bool Interrupted,
    [property: JsonPropertyName("apiVersion")] int ApiVersion,
    [property: JsonPropertyName("authToken")] string AuthToken,
    [property: JsonPropertyName("sendType")] string SendType,
    [property: JsonPropertyName("events")] IReadOnlyCollection<string> Events);

internal sealed record AsaasCustomerBody(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("cpfCnpj")] string CpfCnpj,
    [property: JsonPropertyName("mobilePhone")] string? MobilePhone,
    [property: JsonPropertyName("notificationDisabled")] bool NotificationDisabled);

internal sealed record AsaasIdResponse([property: JsonPropertyName("id")] string? Id);

internal sealed record AsaasSplitBody(
    [property: JsonPropertyName("walletId")] string WalletId,
    [property: JsonPropertyName("percentualValue")] decimal PercentualValue);

internal sealed record AsaasPaymentBody(
    [property: JsonPropertyName("customer")] string Customer,
    [property: JsonPropertyName("billingType")] string BillingType,
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("dueDate")] string DueDate,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("externalReference")] string ExternalReference,
    [property: JsonPropertyName("split")] IReadOnlyList<AsaasSplitBody>? Split);

internal sealed record AsaasPaymentResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("externalReference")] string? ExternalReference,
    [property: JsonPropertyName("invoiceUrl")] string? InvoiceUrl,
    [property: JsonPropertyName("paymentDate")] string? PaymentDate);

internal sealed record AsaasPaymentListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<AsaasPaymentResponse>? Data);

internal sealed record AsaasPixQrCodeResponse(
    [property: JsonPropertyName("encodedImage")] string? EncodedImage,
    [property: JsonPropertyName("payload")] string? Payload);

internal sealed record AsaasAccountStatusResponse(
    [property: JsonPropertyName("general")] string? General,
    [property: JsonPropertyName("commercialInfo")] string? CommercialInfo,
    [property: JsonPropertyName("bankAccountInfo")] string? BankAccountInfo,
    [property: JsonPropertyName("documentation")] string? Documentation);

internal sealed record AsaasErrorResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<AsaasErrorItem>? Errors);

internal sealed record AsaasErrorItem([property: JsonPropertyName("code")] string? Code);
