using System.Text.Json;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Services.Billing;

/// <summary>
/// Maps a set of active modules to the package that matches it. Mirrors the SQL of the B1 backfill
/// (<c>BillingMigrationSql.BackfillSubscriptions</c>): pmoc/os without rentals = manutencao; rentals
/// without pmoc/os plus inventory or catalog = locacao; anything else (including ambiguous mixes) = reservas.
/// </summary>
public static class PackageResolver
{
    public static string FromModules(IEnumerable<string> modules)
    {
        var set = modules.Select(m => m.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var maintenance = set.Contains(PlatformModules.Pmoc) || set.Contains(PlatformModules.WorkOrders);
        var rentals = set.Contains(PlatformModules.Rentals);
        var inventoryOrCatalog = set.Contains(PlatformModules.Inventory) || set.Contains(PlatformModules.Catalog);

        if (maintenance && !rentals)
        {
            return PackageKeys.Manutencao;
        }

        if (rentals && !maintenance && inventoryOrCatalog)
        {
            return PackageKeys.Locacao;
        }

        return PackageKeys.Reservas;
    }
}

/// <summary>Builds the trialing subscription a self-serve signup starts with.</summary>
public static class TrialSubscriptions
{
    /// <summary>
    /// A trialing, provider-billed subscription on the Profissional plan of the chosen package whose period
    /// ends with the tenant trial. Plus its <c>created</c> event; the caller adds both and saves.
    /// </summary>
    public static (TenantSubscription Subscription, SubscriptionEvent Created) Create(
        Tenant tenant,
        PlanCatalog plan,
        DateTimeOffset now)
    {
        var subscription = new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanCatalogId = plan.Id,
            BillingCycle = BillingCycles.Monthly,
            Status = SubscriptionStatuses.Trialing,
            BillingMode = BillingModes.Provider,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = tenant.TrialEndsAt,
        };

        var created = new SubscriptionEvent
        {
            TenantId = tenant.Id,
            SubscriptionId = subscription.Id,
            Type = SubscriptionEventTypes.Created,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = "signup",
                packageKey = plan.PackageKey,
                planKey = plan.PlanKey,
                status = subscription.Status,
            }),
        };

        return (subscription, created);
    }
}
