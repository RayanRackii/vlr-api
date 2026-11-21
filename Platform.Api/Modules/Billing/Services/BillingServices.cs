using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Billing.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Billing.Services;

/// <summary>Price arithmetic shared by the admin subscription view and the billing summary.</summary>
public static class SubscriptionPricing
{
    /// <summary>
    /// Monthly-equivalent price in cents after the discount: monthly price as is, annual price divided by 12.
    /// Rounded half away from zero.
    /// </summary>
    public static decimal MonthlyEquivalentCents(
        string billingCycle,
        int monthlyPriceCents,
        int annualPriceCents,
        int discountPercent)
    {
        var gross = billingCycle == BillingCycles.Annual
            ? annualPriceCents / 12m
            : monthlyPriceCents;
        return gross * (100 - Math.Clamp(discountPercent, 0, 100)) / 100m;
    }

    public static int Round(decimal cents) =>
        (int)Math.Round(cents, MidpointRounding.AwayFromZero);
}

public interface IPlanCatalogService
{
    Task<IReadOnlyList<PublicPlanDto>> ListActiveAsync(CancellationToken cancellationToken);
}

public sealed class PlanCatalogService(AppDbContext dbContext) : IPlanCatalogService
{
    public async Task<IReadOnlyList<PublicPlanDto>> ListActiveAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.PlanCatalogs
            .AsNoTracking()
            .Where(plan => plan.IsActive)
            .OrderBy(plan => plan.SortOrder)
            .ThenBy(plan => plan.PackageKey)
            .ThenBy(plan => plan.PlanKey)
            .ToListAsync(cancellationToken);

        return rows
            .Select(plan => new PublicPlanDto(
                plan.Id,
                plan.PackageKey,
                plan.PlanKey,
                plan.Name,
                plan.MonthlyPriceCents,
                plan.AnnualPriceCents,
                plan.Currency,
                plan.GetModules(),
                ToDto(plan.GetLimits()),
                plan.SortOrder))
            .ToList();
    }

    internal static PlanLimitsDto ToDto(PlanLimitValues limits) =>
        new(limits.Assets, limits.RentableResources, limits.Users, limits.Units, limits.WhatsappMessagesMonth);
}

public interface IAdminSubscriptionService
{
    /// <exception cref="KeyNotFoundException">The tenant does not exist.</exception>
    Task<TenantSubscriptionDto> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    /// <exception cref="KeyNotFoundException">The tenant does not exist.</exception>
    /// <exception cref="ArgumentException">A value is invalid.</exception>
    /// <exception cref="InvalidOperationException">The subscription changed concurrently.</exception>
    Task<TenantSubscriptionDto> PutAsync(
        Guid tenantId,
        PutTenantSubscriptionRequestDto request,
        string? actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// Platform-admin management of a tenant subscription. The billing tables are not tenant scoped, so every
/// query carries the explicit tenant id of the route.
/// </summary>
public sealed class AdminSubscriptionService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IAdminSubscriptionService
{
    private const int EventListSize = 20;

    public const string FundadorCoupon = "fundador";

    public const int FundadorDiscountPercent = 40;

    public const int FundadorMonths = 12;

    public async Task<TenantSubscriptionDto> GetAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        await EnsureTenantExistsAsync(tenantId, cancellationToken);
        var subscription = await LoadCurrentAsync(tenantId, track: false, cancellationToken);
        return await ToDtoAsync(tenantId, subscription, cancellationToken);
    }

    public async Task<TenantSubscriptionDto> PutAsync(
        Guid tenantId,
        PutTenantSubscriptionRequestDto request,
        string? actor,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tenant = await dbContext.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken)
            ?? throw new KeyNotFoundException("Tenant not found.");

        var billingCycle = request.BillingCycle?.Trim().ToLowerInvariant();
        var status = request.Status?.Trim().ToLowerInvariant();
        var billingMode = request.BillingMode?.Trim().ToLowerInvariant();

        if (billingCycle is not null && !BillingCycles.IsKnown(billingCycle))
        {
            throw new ArgumentException("BillingCycle must be monthly or annual.");
        }

        if (status is not null && !SubscriptionStatuses.IsKnown(status))
        {
            throw new ArgumentException(
                $"Status must be one of: {string.Join(", ", SubscriptionStatuses.All)}.");
        }

        if (billingMode is not null && !BillingModes.IsKnown(billingMode))
        {
            throw new ArgumentException("BillingMode must be provider or manual.");
        }

        var (discountPercent, discountUntil) = ResolveDiscount(request, now);

        var subscription = await LoadCurrentAsync(tenantId, track: true, cancellationToken);
        var isNew = subscription is null;
        var before = isNew ? null : Snapshot(subscription!);

        PlanCatalog? plan = null;
        if (request.PlanCatalogId is { } planId)
        {
            plan = await dbContext.PlanCatalogs.FirstOrDefaultAsync(p => p.Id == planId, cancellationToken)
                ?? throw new ArgumentException("Plan not found.");
            if (!plan.IsActive && (isNew || subscription!.PlanCatalogId != planId))
            {
                throw new ArgumentException("Plan is not active.");
            }
        }

        // A read_only status is only enforced on provider-billed rows (see PlanGuard), so a manual row
        // asked to be read-only would silently do nothing: refuse instead of misleading the admin.
        // Validated before any mutation so a refused request leaves the tracked entity untouched.
        var resultingStatus = status ?? (isNew ? SubscriptionStatuses.Active : subscription!.Status);
        var resultingMode = billingMode ?? (isNew ? BillingModes.Manual : subscription!.BillingMode);
        if (resultingStatus == SubscriptionStatuses.ReadOnly && resultingMode != BillingModes.Provider)
        {
            throw new ArgumentException(
                "Status read_only requires BillingMode provider; manually billed subscriptions are never blocked.");
        }

        if (isNew)
        {
            if (plan is null)
            {
                throw new ArgumentException("PlanCatalogId is required to create a subscription.");
            }

            // A subscription created by hand is billed outside the app until a provider takes over.
            subscription = new TenantSubscription
            {
                TenantId = tenantId,
                PlanCatalogId = plan.Id,
                BillingCycle = billingCycle ?? BillingCycles.Monthly,
                Status = status ?? SubscriptionStatuses.Active,
                BillingMode = billingMode ?? BillingModes.Manual,
                CurrentPeriodStart = now,
            };
            dbContext.TenantSubscriptions.Add(subscription);
        }
        else
        {
            if (plan is not null)
            {
                subscription!.PlanCatalogId = plan.Id;
            }

            if (billingCycle is not null)
            {
                subscription!.BillingCycle = billingCycle;
            }

            if (billingMode is not null)
            {
                subscription!.BillingMode = billingMode;
            }

            if (status is not null)
            {
                ApplyStatus(subscription!, status, now);
            }
        }

        if (isNew && subscription!.Status == SubscriptionStatuses.PastDue)
        {
            subscription.PastDueSince = now;
        }

        if (isNew && subscription!.Status == SubscriptionStatuses.Canceled)
        {
            subscription.CanceledAt = now;
        }

        if (request.LimitsUnlimited is { } unlimited)
        {
            subscription!.LimitsUnlimited = unlimited;
        }

        if (discountPercent is { } percent)
        {
            subscription!.DiscountPercent = percent;
            subscription.DiscountUntil = percent == 0 ? null : discountUntil;
        }
        else if (discountUntil is { } until)
        {
            subscription!.DiscountUntil = until;
        }

        ArgumentNullException.ThrowIfNull(subscription);

        // The subscription is the source of truth: an active (paying or manually billed) tenant leaves the
        // trial lifecycle, so neither the read-only mode nor the purge job can apply any more.
        var converted = false;
        if (subscription.Status == SubscriptionStatuses.Active && (tenant.IsTrial || tenant.TrialEndsAt is not null))
        {
            tenant.ConvertFromTrial();
            converted = true;
        }

        var after = Snapshot(subscription);
        var changes = Diff(before, after);
        if (converted)
        {
            changes["tenantTrialConverted"] = new { from = true, to = false };
        }

        if (changes.Count > 0 || isNew)
        {
            if (!isNew)
            {
                subscription.StampConcurrency();
            }

            dbContext.SubscriptionEvents.Add(new SubscriptionEvent
            {
                TenantId = tenantId,
                SubscriptionId = subscription.Id,
                Type = EventType(isNew, before, after),
                PayloadJson = JsonSerializer.Serialize(new { source = "admin", actor, changes }),
            });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("The subscription was changed by someone else. Reload and retry.");
        }
        catch (DbUpdateException ex) when (isNew)
        {
            throw new InvalidOperationException("The tenant already has a current subscription.", ex);
        }

        return await ToDtoAsync(tenantId, subscription, cancellationToken);
    }

    private static void ApplyStatus(TenantSubscription subscription, string status, DateTimeOffset now)
    {
        var previous = subscription.Status;
        subscription.Status = status;
        if (status == previous)
        {
            return;
        }

        subscription.PastDueSince = status == SubscriptionStatuses.PastDue ? now : null;
        subscription.CanceledAt = status == SubscriptionStatuses.Canceled ? now : null;

        if (status == SubscriptionStatuses.Active)
        {
            // Coming into force by hand: a new period starts now; the end is unknown until a provider sets it.
            subscription.CurrentPeriodStart = now;
            subscription.CurrentPeriodEnd = null;
        }
    }

    private static (int? Percent, DateTimeOffset? Until) ResolveDiscount(
        PutTenantSubscriptionRequestDto request,
        DateTimeOffset now)
    {
        var coupon = request.Coupon?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(coupon))
        {
            if (coupon != FundadorCoupon)
            {
                throw new ArgumentException("Unknown coupon.");
            }

            if (request.DiscountPercent is not null || request.DiscountUntil is not null)
            {
                throw new ArgumentException("A coupon cannot be combined with DiscountPercent or DiscountUntil.");
            }

            return (FundadorDiscountPercent, now.AddMonths(FundadorMonths));
        }

        if (request.DiscountPercent is { } percent && percent is < 0 or > 100)
        {
            throw new ArgumentException("DiscountPercent must be between 0 and 100.");
        }

        return (request.DiscountPercent, request.DiscountUntil);
    }

    private static string EventType(bool isNew, Dictionary<string, object?>? before, Dictionary<string, object?> after)
    {
        if (isNew)
        {
            return SubscriptionEventTypes.Created;
        }

        var from = before?["status"] as string;
        var to = after["status"] as string;
        if (from != to)
        {
            if (to == SubscriptionStatuses.Canceled)
            {
                return SubscriptionEventTypes.Canceled;
            }

            if (to == SubscriptionStatuses.ReadOnly)
            {
                return SubscriptionEventTypes.ReadOnly;
            }

            if (to == SubscriptionStatuses.Active && from == SubscriptionStatuses.ReadOnly)
            {
                return SubscriptionEventTypes.Reactivated;
            }
        }

        return SubscriptionEventTypes.PlanChanged;
    }

    private static Dictionary<string, object?> Snapshot(TenantSubscription s) => new()
    {
        ["planCatalogId"] = s.PlanCatalogId,
        ["billingCycle"] = s.BillingCycle,
        ["status"] = s.Status,
        ["billingMode"] = s.BillingMode,
        ["limitsUnlimited"] = s.LimitsUnlimited,
        ["discountPercent"] = s.DiscountPercent,
        ["discountUntil"] = s.DiscountUntil,
    };

    private static Dictionary<string, object?> Diff(
        Dictionary<string, object?>? before,
        Dictionary<string, object?> after)
    {
        var changes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in after)
        {
            object? old = null;
            before?.TryGetValue(key, out old);
            if (!Equals(old, value))
            {
                changes[key] = new { from = old, to = value };
            }
        }

        return changes;
    }

    private async Task EnsureTenantExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Tenants.AsNoTracking().AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            throw new KeyNotFoundException("Tenant not found.");
        }
    }

    private Task<TenantSubscription?> LoadCurrentAsync(
        Guid tenantId,
        bool track,
        CancellationToken cancellationToken)
    {
        var query = dbContext.TenantSubscriptions
            .Where(s => s.TenantId == tenantId && s.EndedAt == null)
            .OrderByDescending(s => s.CreatedAt);
        return (track ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<TenantSubscriptionDto> ToDtoAsync(
        Guid tenantId,
        TenantSubscription? subscription,
        CancellationToken cancellationToken)
    {
        if (subscription is null)
        {
            return new TenantSubscriptionDto(
                tenantId, false, null, null, null, null, null, null, null, null, false,
                null, null, 0, null, 0, 0, null, null, null, null, null, []);
        }

        var plan = await dbContext.PlanCatalogs.AsNoTracking()
            .FirstAsync(p => p.Id == subscription.PlanCatalogId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var effective = subscription.EffectiveDiscountPercent(now);
        var events = (await dbContext.SubscriptionEvents
                .AsNoTracking()
                .Where(e => e.SubscriptionId == subscription.Id)
                .Where(e => !e.Type.StartsWith(SubscriptionEventTypes.DunningPrefix))
                .OrderByDescending(e => e.CreatedAt)
                .Take(EventListSize)
                .ToListAsync(cancellationToken))
            .Select(e => new SubscriptionEventDto(e.Id, e.Type, e.CreatedAt))
            .ToList();

        return new TenantSubscriptionDto(
            tenantId,
            true,
            subscription.Id,
            plan.Id,
            plan.PackageKey,
            plan.PlanKey,
            plan.Name,
            subscription.BillingCycle,
            subscription.Status,
            subscription.BillingMode,
            subscription.LimitsUnlimited,
            subscription.CurrentPeriodStart,
            subscription.CurrentPeriodEnd,
            subscription.DiscountPercent,
            subscription.DiscountUntil,
            effective,
            SubscriptionPricing.Round(SubscriptionPricing.MonthlyEquivalentCents(
                subscription.BillingCycle, plan.MonthlyPriceCents, plan.AnnualPriceCents, effective)),
            plan.Currency,
            subscription.Provider,
            subscription.PastDueSince,
            subscription.CanceledAt,
            PlanCatalogService.ToDto(plan.GetLimits()),
            events);
    }
}

public interface IAdminBillingSummaryService
{
    Task<BillingSummaryDto> GetAsync(CancellationToken cancellationToken);
}

public sealed class AdminBillingSummaryService(
    AppDbContext dbContext,
    TimeProvider timeProvider) : IAdminBillingSummaryService
{
    public async Task<BillingSummaryDto> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rows = await (
                from subscription in dbContext.TenantSubscriptions.AsNoTracking()
                join plan in dbContext.PlanCatalogs.AsNoTracking()
                    on subscription.PlanCatalogId equals plan.Id
                where subscription.EndedAt == null
                select new
                {
                    plan.PackageKey,
                    plan.Currency,
                    plan.MonthlyPriceCents,
                    plan.AnnualPriceCents,
                    subscription.Status,
                    subscription.BillingCycle,
                    subscription.DiscountPercent,
                    subscription.DiscountUntil,
                })
            .ToListAsync(cancellationToken);

        decimal Mrr(string status, string cycle, int monthly, int annual, int discount, DateTimeOffset? until) =>
            status is SubscriptionStatuses.Active or SubscriptionStatuses.PastDue
                ? SubscriptionPricing.MonthlyEquivalentCents(
                    cycle, monthly, annual, until is { } end && end <= now ? 0 : discount)
                : 0m;

        var packages = PackageKeys.All
            .Concat(rows.Select(r => r.PackageKey).Except(PackageKeys.All))
            .Select(key =>
            {
                var scoped = rows.Where(r => r.PackageKey == key).ToList();
                return new BillingPackageSummaryDto(
                    key,
                    scoped.Count(r => r.Status == SubscriptionStatuses.Trialing),
                    scoped.Count(r => r.Status == SubscriptionStatuses.Active),
                    scoped.Count(r => r.Status == SubscriptionStatuses.PastDue),
                    scoped.Count(r => r.Status == SubscriptionStatuses.ReadOnly),
                    (long)Math.Round(
                        scoped.Sum(r => Mrr(
                            r.Status, r.BillingCycle, r.MonthlyPriceCents, r.AnnualPriceCents,
                            r.DiscountPercent, r.DiscountUntil)),
                        MidpointRounding.AwayFromZero));
            })
            .ToList();

        var totalMrr = (long)Math.Round(
            rows.Sum(r => Mrr(
                r.Status, r.BillingCycle, r.MonthlyPriceCents, r.AnnualPriceCents,
                r.DiscountPercent, r.DiscountUntil)),
            MidpointRounding.AwayFromZero);

        return new BillingSummaryDto(
            rows.Select(r => r.Currency).FirstOrDefault() ?? "BRL",
            totalMrr,
            rows.Count(r => r.Status == SubscriptionStatuses.Trialing),
            rows.Count(r => r.Status == SubscriptionStatuses.Active),
            rows.Count(r => r.Status == SubscriptionStatuses.PastDue),
            rows.Count(r => r.Status == SubscriptionStatuses.ReadOnly),
            packages);
    }
}
