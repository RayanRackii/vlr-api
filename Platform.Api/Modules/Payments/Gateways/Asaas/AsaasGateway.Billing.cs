using System.Globalization;
using System.Text.Json;

namespace Platform.Api.Modules.Payments.Gateways.Asaas;

// Platform billing: Rolvix charging its tenants through the root account (no subaccount key involved).
public sealed partial class AsaasGateway
{
    public string ProviderKey => "asaas";

    public async Task<string> CreateOrGetCustomerAsync(
        BillingCustomerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rootKey = RequireRootKey();

        var existing = await SendAsync<AsaasListResponse<AsaasIdResponse>>(
            HttpMethod.Get,
            $"customers?externalReference={Uri.EscapeDataString(request.ExternalReference)}",
            rootKey,
            null,
            cancellationToken);
        var found = existing.Data?.FirstOrDefault(customer => !string.IsNullOrEmpty(customer.Id));
        if (found is not null)
        {
            return found.Id!;
        }

        var body = new AsaasBillingCustomerBody(
            request.Name,
            request.Email,
            request.CpfCnpj,
            request.MobilePhone,
            request.ExternalReference,
            request.NotificationDisabled);
        var created = await SendAsync<AsaasIdResponse>(HttpMethod.Post, "customers", rootKey, body, cancellationToken);
        return string.IsNullOrEmpty(created.Id)
            ? throw new PaymentGatewayException("invalid_response", 200)
            : created.Id;
    }

    public async Task<BillingSubscriptionInfo> CreateSubscriptionAsync(
        BillingSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rootKey = RequireRootKey();

        // Retry safety: a previous attempt may have created the subscription before the answer was lost.
        var existing = await SendAsync<AsaasListResponse<AsaasSubscriptionResponse>>(
            HttpMethod.Get,
            $"subscriptions?customer={Uri.EscapeDataString(request.CustomerId)}"
            + $"&externalReference={Uri.EscapeDataString(request.ExternalReference)}&status=ACTIVE",
            rootKey,
            null,
            cancellationToken);
        var found = existing.Data?.FirstOrDefault(
            subscription => !string.IsNullOrEmpty(subscription.Id) && subscription.Deleted != true);
        if (found is not null)
        {
            return MapSubscription(found);
        }

        var body = new AsaasSubscriptionBody(
            request.CustomerId,
            ToWire(request.Method),
            request.Value,
            request.NextDueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ToWire(request.Cycle),
            request.Description,
            request.ExternalReference);
        var created = await SendAsync<AsaasSubscriptionResponse>(
            HttpMethod.Post, "subscriptions", rootKey, body, cancellationToken);
        return MapSubscription(created);
    }

    public async Task<BillingSubscriptionInfo> UpdateSubscriptionAsync(
        string subscriptionId,
        BillingSubscriptionUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(update);

        var body = new AsaasSubscriptionUpdateBody(
            update.Value,
            update.Cycle is { } cycle ? ToWire(cycle) : null,
            update.Method is { } method ? ToWire(method) : null,
            update.UpdatePendingPayments);
        var updated = await SendAsync<AsaasSubscriptionResponse>(
            HttpMethod.Put,
            $"subscriptions/{Uri.EscapeDataString(subscriptionId)}",
            RequireRootKey(),
            body,
            cancellationToken);
        return MapSubscription(updated);
    }

    public async Task CancelSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        try
        {
            await SendAsync<JsonElement>(
                HttpMethod.Delete,
                $"subscriptions/{Uri.EscapeDataString(subscriptionId)}",
                RequireRootKey(),
                null,
                cancellationToken);
        }
        catch (PaymentGatewayException ex) when (ex.HttpStatus == 404)
        {
            // Already gone: the goal (no subscription) is met, so a retry of a cancel is harmless.
        }
    }

    public async Task<BillingSubscriptionInfo> GetSubscriptionAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var subscription = await SendAsync<AsaasSubscriptionResponse>(
            HttpMethod.Get,
            $"subscriptions/{Uri.EscapeDataString(subscriptionId)}",
            RequireRootKey(),
            null,
            cancellationToken);
        return MapSubscription(subscription);
    }

    public async Task<BillingPayment> GetPaymentAsync(string paymentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);

        var payment = await SendAsync<AsaasBillingPaymentResponse>(
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(paymentId)}",
            RequireRootKey(),
            null,
            cancellationToken);
        return MapBillingPayment(payment);
    }

    public async Task<IReadOnlyList<BillingPayment>> ListSubscriptionPaymentsAsync(
        string subscriptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var list = await SendAsync<AsaasListResponse<AsaasBillingPaymentResponse>>(
            HttpMethod.Get,
            $"subscriptions/{Uri.EscapeDataString(subscriptionId)}/payments",
            RequireRootKey(),
            null,
            cancellationToken);
        return (list.Data ?? []).Select(MapBillingPayment).ToList();
    }

    public async Task<BillingPixQrCode> GetPaymentPixQrCodeAsync(
        string paymentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);

        var qr = await SendAsync<AsaasPixQrCodeResponse>(
            HttpMethod.Get,
            $"payments/{Uri.EscapeDataString(paymentId)}/pixQrCode",
            RequireRootKey(),
            null,
            cancellationToken);
        return string.IsNullOrEmpty(qr.Payload) || string.IsNullOrEmpty(qr.EncodedImage)
            ? throw new PaymentGatewayException("invalid_response", 200)
            : new BillingPixQrCode(qr.EncodedImage, qr.Payload);
    }

    private string RequireRootKey()
    {
        var rootKey = _options.CurrentValue.RootApiKey;
        return string.IsNullOrWhiteSpace(rootKey)
            ? throw new PaymentConfigurationException("Payments:Asaas:RootApiKey is not configured.")
            : rootKey;
    }

    private static BillingSubscriptionInfo MapSubscription(AsaasSubscriptionResponse subscription)
    {
        if (string.IsNullOrEmpty(subscription.Id))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        var state = (subscription.Status ?? string.Empty).ToUpperInvariant() switch
        {
            "ACTIVE" => BillingSubscriptionState.Active,
            "INACTIVE" => BillingSubscriptionState.Inactive,
            "EXPIRED" => BillingSubscriptionState.Expired,
            _ => BillingSubscriptionState.Unknown,
        };

        BillingCycleKind? cycle = (subscription.Cycle ?? string.Empty).ToUpperInvariant() switch
        {
            "MONTHLY" => BillingCycleKind.Monthly,
            "YEARLY" => BillingCycleKind.Yearly,
            _ => null,
        };

        return new BillingSubscriptionInfo(
            subscription.Id,
            subscription.Customer,
            state,
            subscription.Deleted == true,
            subscription.Value,
            cycle,
            ToMethod(subscription.BillingType),
            ParseDate(subscription.NextDueDate),
            subscription.ExternalReference);
    }

    private static BillingPayment MapBillingPayment(AsaasBillingPaymentResponse payment)
    {
        if (string.IsNullOrEmpty(payment.Id))
        {
            throw new PaymentGatewayException("invalid_response", 200);
        }

        return new BillingPayment(
            payment.Id,
            ToChargeStatus(payment.Status),
            payment.Value,
            ParseDate(payment.DueDate),
            ParseDate(payment.PaymentDate),
            payment.InvoiceUrl,
            payment.Subscription,
            payment.Customer,
            ToMethod(payment.BillingType));
    }

    private static BillingMethod? ToMethod(string? billingType) =>
        (billingType ?? string.Empty).ToUpperInvariant() switch
        {
            "PIX" => BillingMethod.Pix,
            "CREDIT_CARD" => BillingMethod.CreditCard,
            "BOLETO" => BillingMethod.Boleto,
            _ => null,
        };

    private static string ToWire(BillingMethod method) => method switch
    {
        BillingMethod.Pix => "PIX",
        BillingMethod.CreditCard => "CREDIT_CARD",
        BillingMethod.Boleto => "BOLETO",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    private static string ToWire(BillingCycleKind cycle) => cycle switch
    {
        BillingCycleKind.Monthly => "MONTHLY",
        BillingCycleKind.Yearly => "YEARLY",
        _ => throw new ArgumentOutOfRangeException(nameof(cycle)),
    };
}
