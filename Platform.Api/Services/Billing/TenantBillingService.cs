using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Api.Modules.Billing.Services;
using Platform.Api.Modules.Payments;
using Platform.Api.Modules.Payments.Gateways;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Time;

namespace Platform.Api.Services.Billing;

/// <summary>
/// The tenant staff side of Rolvix billing: subscribe (checkout), change plan and cancel. Every query carries the
/// explicit tenant id because the billing tables are not tenant scoped. Provider failures surface as
/// <see cref="BillingException"/> (502/503) that never carry a provider body or a key.
/// </summary>
public interface ITenantBillingService
{
    /// <exception cref="BillingException">The request cannot proceed (validation, state, provider).</exception>
    Task<CheckoutResponseDto> CheckoutAsync(
        Guid tenantId,
        string? actorEmail,
        CheckoutRequestDto request,
        CancellationToken cancellationToken);

    /// <summary>
    /// The checkout answer of a subscription created earlier (an idempotent replay): the invoice still open.
    /// Null when the subscription is gone or has nothing left to pay.
    /// </summary>
    Task<CheckoutResponseDto?> LoadCheckoutAsync(
        Guid tenantId,
        Guid subscriptionId,
        string? method,
        CancellationToken cancellationToken);

    /// <exception cref="BillingException">The request cannot proceed (validation, state, usage, provider).</exception>
    Task ChangePlanAsync(Guid tenantId, ChangePlanRequestDto request, CancellationToken cancellationToken);

    /// <exception cref="BillingException">The request cannot proceed (state, provider).</exception>
    Task CancelAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenantBillingService(
    AppDbContext dbContext,
    IPlatformBillingGateway gateway,
    IPlanUsage planUsage,
    IPlanModuleEnabler moduleEnabler,
    TimeProvider timeProvider,
    ILogger<TenantBillingService> logger) : ITenantBillingService
{
    public async Task<CheckoutResponseDto> CheckoutAsync(
        Guid tenantId,
        string? actorEmail,
        CheckoutRequestDto request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cycle = ParseCycle(request.BillingCycle);
        var method = ParseMethod(request.Method);
        var now = timeProvider.GetUtcNow();

        var plan = await dbContext.PlanCatalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PlanCatalogId && p.IsActive, cancellationToken)
            ?? throw new BillingException(BillingErrorCodes.PlanNotFound, 400, "Plan not found.");

        var subscription = await LoadCurrentAsync(tenantId, cancellationToken)
            ?? throw new BillingException(
                BillingErrorCodes.NoSubscription,
                409,
                "This workspace has no subscription yet. Contact support to set it up.");

        // A live provider subscription is resumed (same choice) or refused (another one: change plan instead).
        // A cancelled one (or one the tenant already asked to cancel) is gone at the provider.
        if (subscription.ProviderSubscriptionId is not null
            && subscription.Status != SubscriptionStatuses.Canceled
            && !subscription.CancelAtPeriodEnd)
        {
            var samePlan = (subscription.PendingPlanCatalogId ?? subscription.PlanCatalogId) == plan.Id;
            var sameCycle = (subscription.PendingBillingCycle ?? subscription.BillingCycle) == cycle;
            if (!samePlan || !sameCycle)
            {
                throw new BillingException(
                    BillingErrorCodes.AlreadySubscribed,
                    409,
                    "This workspace already has a subscription. Change the plan instead.");
            }

            return await BuildCheckoutResponseAsync(subscription.Id, subscription.ProviderSubscriptionId, method, cancellationToken);
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new BillingException(BillingErrorCodes.NoSubscription, 409, "Tenant not found.");

        var discount = subscription.EffectiveDiscountPercent(now);
        var chargeCents = BillingPricing.ChargeCents(plan, cycle, discount);
        if (chargeCents <= 0)
        {
            throw new BillingException(
                BillingErrorCodes.NothingToCharge,
                409,
                "There is nothing to charge for this plan. Contact support.");
        }

        var nextDue = NextDueDate(subscription, tenant, now);

        var info = await ProviderAsync(async () =>
        {
            var customerId = subscription.ProviderCustomerId
                ?? await gateway.CreateOrGetCustomerAsync(CustomerFor(tenant, actorEmail), cancellationToken);
            var created = await gateway.CreateSubscriptionAsync(
                new BillingSubscriptionRequest(
                    customerId,
                    ToKind(cycle),
                    method,
                    BillingPricing.ProviderValue(chargeCents),
                    nextDue,
                    $"Rolvix {plan.Name} ({cycle})",
                    tenantId.ToString()),
                cancellationToken);
            return (CustomerId: customerId, Subscription: created);
        });

        subscription.BillingMode = BillingModes.Provider;
        subscription.Provider = gateway.ProviderKey;
        subscription.ProviderCustomerId = info.CustomerId;
        subscription.ProviderSubscriptionId = info.Subscription.Id;
        subscription.CancelAtPeriodEnd = false;

        // The plan the tenant picked starts with the first payment (the payment webhook applies it).
        if (subscription.PlanCatalogId != plan.Id || subscription.BillingCycle != cycle)
        {
            subscription.PendingPlanCatalogId = plan.Id;
            subscription.PendingBillingCycle = cycle;
        }
        else
        {
            subscription.PendingPlanCatalogId = null;
            subscription.PendingBillingCycle = null;
        }

        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = tenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.Created,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "checkout",
                planCatalogId = plan.Id,
                billingCycle = cycle,
                method = method.ToString(),
                valueCents = chargeCents,
                providerSubscriptionId = info.Subscription.Id,
            }),
        });

        await SaveAsync(cancellationToken);

        return await BuildCheckoutResponseAsync(subscription.Id, info.Subscription.Id, method, cancellationToken);
    }

    public async Task<CheckoutResponseDto?> LoadCheckoutAsync(
        Guid tenantId,
        Guid subscriptionId,
        string? method,
        CancellationToken cancellationToken)
    {
        var subscription = await dbContext.TenantSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subscriptionId && s.TenantId == tenantId, cancellationToken);
        if (subscription?.ProviderSubscriptionId is null)
        {
            return null;
        }

        try
        {
            return await BuildCheckoutResponseAsync(
                subscription.Id,
                subscription.ProviderSubscriptionId,
                ParseMethod(method),
                cancellationToken);
        }
        catch (BillingException ex) when (ex.Code == BillingErrorCodes.NoOpenInvoice)
        {
            return null;
        }
    }

    public async Task ChangePlanAsync(Guid tenantId, ChangePlanRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cycle = ParseCycle(request.BillingCycle);
        var now = timeProvider.GetUtcNow();

        var target = await dbContext.PlanCatalogs
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PlanCatalogId && p.IsActive, cancellationToken)
            ?? throw new BillingException(BillingErrorCodes.PlanNotFound, 400, "Plan not found.");

        var subscription = await LoadCurrentAsync(tenantId, cancellationToken)
            ?? throw new BillingException(BillingErrorCodes.NoSubscription, 409, "This workspace has no subscription.");

        if (subscription.BillingMode != BillingModes.Provider
            || subscription.ProviderSubscriptionId is not { } providerSubscriptionId
            || subscription.Status != SubscriptionStatuses.Active
            || subscription.CancelAtPeriodEnd)
        {
            throw new BillingException(
                BillingErrorCodes.NoActiveSubscription,
                409,
                "Only an active subscription billed through Rolvix can change plan.");
        }

        var current = await dbContext.PlanCatalogs
            .AsNoTracking()
            .FirstAsync(p => p.Id == subscription.PlanCatalogId, cancellationToken);
        if (!string.Equals(current.Currency, target.Currency, StringComparison.Ordinal))
        {
            throw new BillingException(BillingErrorCodes.CurrencyMismatch, 400, "The plans use different currencies.");
        }

        var discount = subscription.EffectiveDiscountPercent(now);

        if (target.Id == subscription.PlanCatalogId && cycle == subscription.BillingCycle)
        {
            await CancelScheduledChangeAsync(subscription, current, providerSubscriptionId, discount, now, cancellationToken);
            return;
        }

        var targetCents = BillingPricing.ChargeCents(target, cycle, discount);
        if (targetCents <= 0)
        {
            throw new BillingException(
                BillingErrorCodes.NothingToCharge,
                409,
                "There is nothing to charge for this plan. Contact support.");
        }

        var newMonthly = SubscriptionPricing.MonthlyEquivalentCents(
            cycle, target.MonthlyPriceCents, target.AnnualPriceCents, discount);
        var currentMonthly = SubscriptionPricing.MonthlyEquivalentCents(
            subscription.BillingCycle, current.MonthlyPriceCents, current.AnnualPriceCents, discount);
        var isDowngrade = newMonthly < currentMonthly;

        if (isDowngrade)
        {
            if (!subscription.LimitsUnlimited)
            {
                var usage = await planUsage.GetAsync(tenantId, cancellationToken);
                if (usage.FirstViolation(target.GetLimits()) is { } violation)
                {
                    throw new BillingException(
                        BillingErrorCodes.UsageAboveLimit,
                        409,
                        $"The {target.PlanKey} plan allows at most {violation.Max} "
                        + $"{violation.Limit.Replace('_', ' ')}; the workspace uses {violation.Current}.",
                        new Dictionary<string, object?>
                        {
                            ["limit"] = violation.Limit,
                            ["current"] = violation.Current,
                            ["max"] = violation.Max,
                            ["planKey"] = target.PlanKey,
                        });
                }
            }

            // Future charges at the new price; the charge already issued keeps the price the tenant is paying.
            await ProviderAsync(() => gateway.UpdateSubscriptionAsync(
                providerSubscriptionId,
                new BillingSubscriptionUpdate(
                    BillingPricing.ProviderValue(targetCents), ToKind(cycle), null, UpdatePendingPayments: false),
                cancellationToken));

            subscription.PendingPlanCatalogId = target.Id;
            subscription.PendingBillingCycle = cycle;
            subscription.StampConcurrency();
            dbContext.SubscriptionEvents.Add(PlanChangedEvent(
                tenantId, subscription.Id, "downgrade", current, target, cycle, targetCents, scheduledFor: subscription.CurrentPeriodEnd, enabled: []));
            await SaveAsync(cancellationToken);
            return;
        }

        await ProviderAsync(() => gateway.UpdateSubscriptionAsync(
            providerSubscriptionId,
            new BillingSubscriptionUpdate(
                BillingPricing.ProviderValue(targetCents), ToKind(cycle), null, UpdatePendingPayments: true),
            cancellationToken));

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var enabled = await moduleEnabler.StageAsync(tenantId, target.GetModules(), cancellationToken);
        subscription.PlanCatalogId = target.Id;
        subscription.BillingCycle = cycle;
        subscription.PendingPlanCatalogId = null;
        subscription.PendingBillingCycle = null;
        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(PlanChangedEvent(
            tenantId, subscription.Id, "upgrade", current, target, cycle, targetCents, scheduledFor: null, enabled));

        await SaveAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        await moduleEnabler.AfterCommitAsync(tenantId, enabled, cancellationToken);
    }

    public async Task CancelAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var subscription = await LoadCurrentAsync(tenantId, cancellationToken)
            ?? throw new BillingException(BillingErrorCodes.NoSubscription, 409, "This workspace has no subscription.");

        if (subscription.BillingMode != BillingModes.Provider || subscription.ProviderSubscriptionId is not { } providerSubscriptionId)
        {
            if (subscription.CancelAtPeriodEnd || subscription.Status == SubscriptionStatuses.Canceled)
            {
                return;
            }

            throw new BillingException(
                BillingErrorCodes.NoActiveSubscription,
                409,
                "There is no subscription billed through Rolvix to cancel.");
        }

        if (subscription.CancelAtPeriodEnd || subscription.Status == SubscriptionStatuses.Canceled)
        {
            return;
        }

        await ProviderVoidAsync(() => gateway.CancelSubscriptionAsync(providerSubscriptionId, cancellationToken));

        string effect;
        switch (subscription.Status)
        {
            case SubscriptionStatuses.Trialing:
                // Nothing was ever paid: drop the provider subscription of the unfinished checkout, keep the trial.
                subscription.ProviderSubscriptionId = null;
                subscription.PendingPlanCatalogId = null;
                subscription.PendingBillingCycle = null;
                effect = "checkout_abandoned";
                break;

            case SubscriptionStatuses.ReadOnly:
                // The paid period is over already: nothing left to wait for.
                subscription.Status = SubscriptionStatuses.Canceled;
                subscription.CanceledAt = now;
                subscription.PendingPlanCatalogId = null;
                subscription.PendingBillingCycle = null;
                effect = "immediate";
                break;

            default:
                // Active or past due: the plan stays until the paid period ends (the period-end job then makes
                // the workspace read-only). Data is kept.
                subscription.CancelAtPeriodEnd = true;
                subscription.CanceledAt = now;
                subscription.PendingPlanCatalogId = null;
                subscription.PendingBillingCycle = null;
                effect = "period_end";
                break;
        }

        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = tenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.Canceled,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "tenant",
                effect,
                effectiveAt = effect == "period_end" ? subscription.CurrentPeriodEnd : now,
            }),
        });

        await SaveAsync(cancellationToken);
    }

    private async Task CancelScheduledChangeAsync(
        TenantSubscription subscription,
        PlanCatalog current,
        string providerSubscriptionId,
        int discount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (subscription.PendingPlanCatalogId is null)
        {
            return;
        }

        // Put the provider back on the price of the plan in force.
        var cents = BillingPricing.ChargeCents(current, subscription.BillingCycle, discount);
        await ProviderAsync(() => gateway.UpdateSubscriptionAsync(
            providerSubscriptionId,
            new BillingSubscriptionUpdate(
                BillingPricing.ProviderValue(cents), ToKind(subscription.BillingCycle), null, UpdatePendingPayments: false),
            cancellationToken));

        subscription.PendingPlanCatalogId = null;
        subscription.PendingBillingCycle = null;
        subscription.StampConcurrency();
        dbContext.SubscriptionEvents.Add(new SubscriptionEvent
        {
            TenantId = subscription.TenantId,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.PlanChanged,
            PayloadJson = JsonSerializer.Serialize(new { source = "tenant", direction = "scheduled_change_cancelled", at = now }),
        });
        await SaveAsync(cancellationToken);
    }

    private async Task<CheckoutResponseDto> BuildCheckoutResponseAsync(
        Guid subscriptionId,
        string providerSubscriptionId,
        BillingMethod method,
        CancellationToken cancellationToken)
    {
        var open = await FindOpenPaymentAsync(providerSubscriptionId, cancellationToken);

        // A retry may ask for another way to pay: move the open charge to it before returning it.
        if (open.Method is { } openMethod && openMethod != method)
        {
            await ProviderAsync(() => gateway.UpdateSubscriptionAsync(
                providerSubscriptionId,
                new BillingSubscriptionUpdate(Method: method, UpdatePendingPayments: true),
                cancellationToken));
            open = await FindOpenPaymentAsync(providerSubscriptionId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(open.InvoiceUrl))
        {
            throw new BillingException(BillingErrorCodes.ProviderError, 502, "The payment provider did not return an invoice.");
        }

        string? qrImage = null;
        string? qrCode = null;
        if (method == BillingMethod.Pix)
        {
            var qr = await ProviderAsync(() => gateway.GetPaymentPixQrCodeAsync(open.Id, cancellationToken));
            qrImage = qr.EncodedImage;
            qrCode = qr.Payload;
        }

        return new CheckoutResponseDto(open.InvoiceUrl, qrImage, qrCode) { SubscriptionId = subscriptionId };
    }

    private async Task<BillingPayment> FindOpenPaymentAsync(string providerSubscriptionId, CancellationToken cancellationToken)
    {
        var payments = await ProviderAsync(() => gateway.ListSubscriptionPaymentsAsync(providerSubscriptionId, cancellationToken));
        return payments
                .Where(p => p.Status is GatewayChargeStatus.Pending or GatewayChargeStatus.Overdue)
                .OrderBy(p => p.DueDate ?? DateOnly.MaxValue)
                .FirstOrDefault()
            ?? throw new BillingException(
                BillingErrorCodes.NoOpenInvoice,
                409,
                "There is no open invoice to pay for this subscription.");
    }

    private DateOnly NextDueDate(TenantSubscription subscription, Tenant tenant, DateTimeOffset now)
    {
        var today = BrazilTimeZone.GetToday(timeProvider);

        // The trial is honoured: the first charge is due when it ends (the tenant can still pay earlier).
        if (subscription.Status == SubscriptionStatuses.Trialing
            && tenant.TrialEndsAt is { } trialEnd
            && trialEnd > now)
        {
            var due = BrazilTimeZone.GetCivilDate(trialEnd);
            return due > today ? due : today;
        }

        // Re-subscribing while a cancelled period is still paid for: no double charge before it ends.
        if (subscription.CancelAtPeriodEnd
            && subscription.CurrentPeriodEnd is { } periodEnd
            && periodEnd > now)
        {
            var due = BrazilTimeZone.GetCivilDate(periodEnd);
            return due > today ? due : today;
        }

        return today;
    }

    private BillingCustomerRequest CustomerFor(Tenant tenant, string? actorEmail)
    {
        var email = !string.IsNullOrWhiteSpace(tenant.ContactEmail) ? tenant.ContactEmail : actorEmail;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new BillingException(
                BillingErrorCodes.ContactEmailRequired,
                400,
                "Set the company contact e-mail before subscribing.");
        }

        var document = new string((tenant.TaxId ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (document.Length is not (11 or 14))
        {
            throw new BillingException(
                BillingErrorCodes.TaxIdInvalid,
                400,
                "The company CPF or CNPJ must be valid before subscribing.");
        }

        var phone = new string((tenant.ContactPhone ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (phone.StartsWith("55", StringComparison.Ordinal) && phone.Length >= 12)
        {
            phone = phone[2..];
        }

        return new BillingCustomerRequest(
            string.IsNullOrWhiteSpace(tenant.TradeName) ? tenant.LegalName : tenant.TradeName,
            email.Trim(),
            document,
            tenant.Id.ToString(),
            phone.Length >= 10 ? phone : null);
    }

    private static SubscriptionEvent PlanChangedEvent(
        Guid tenantId,
        Guid subscriptionId,
        string direction,
        PlanCatalog from,
        PlanCatalog to,
        string cycle,
        int chargeCents,
        DateTimeOffset? scheduledFor,
        IReadOnlyList<string> enabled) =>
        new()
        {
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            Type = SubscriptionEventTypes.PlanChanged,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "tenant",
                direction,
                fromPlanCatalogId = from.Id,
                toPlanCatalogId = to.Id,
                billingCycle = cycle,
                valueCents = chargeCents,
                scheduledFor,
                modulesEnabled = enabled,
            }),
        };

    private Task<TenantSubscription?> LoadCurrentAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TenantSubscriptions
            .Where(s => s.TenantId == tenantId && s.EndedAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new BillingException(
                BillingErrorCodes.ConcurrentChange,
                409,
                "The subscription was changed by someone else. Reload and retry.");
        }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Could not save a billing change.");
            throw new BillingException(
                BillingErrorCodes.ConcurrentChange,
                409,
                "The subscription could not be saved. Reload and retry.");
        }
    }

    private async Task<T> ProviderAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (PaymentConfigurationException)
        {
            throw new BillingException(BillingErrorCodes.NotConfigured, 503, "Billing is not available right now.");
        }
        catch (PaymentGatewayException ex)
        {
            logger.LogWarning("Billing provider call failed: HTTP {Status}, code {Code}.", ex.HttpStatus, ex.Code);
            throw new BillingException(BillingErrorCodes.ProviderError, 502, "The payment provider could not complete the request.");
        }
    }

    private async Task ProviderVoidAsync(Func<Task> call) =>
        await ProviderAsync(async () =>
        {
            await call();
            return true;
        });

    private static string ParseCycle(string? value)
    {
        var cycle = value?.Trim().ToLowerInvariant();
        return BillingCycles.IsKnown(cycle)
            ? cycle!
            : throw new BillingException(BillingErrorCodes.InvalidCycle, 400, "BillingCycle must be monthly or annual.");
    }

    private static BillingMethod ParseMethod(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "pix" => BillingMethod.Pix,
            "credit_card" => BillingMethod.CreditCard,
            "boleto" => BillingMethod.Boleto,
            _ => throw new BillingException(BillingErrorCodes.InvalidMethod, 400, "Method must be pix, credit_card or boleto."),
        };

    private static BillingCycleKind ToKind(string cycle) =>
        cycle == BillingCycles.Annual ? BillingCycleKind.Yearly : BillingCycleKind.Monthly;
}
