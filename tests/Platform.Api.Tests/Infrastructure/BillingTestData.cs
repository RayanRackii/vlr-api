using System.Text.Json;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Infrastructure;

/// <summary>
/// Test helpers for the billing tables. <see cref="CatalogRows"/> mirrors the seed of
/// <c>AddBillingPlansAndSubscriptions</c> (D1 prices, D2 modules, D3 limits); a Postgres test proves the
/// migration SQL produces exactly these rows.
/// </summary>
internal static class BillingTestData
{
    internal sealed record CatalogRow(
        string Package,
        string Plan,
        int Monthly,
        int Annual,
        string[] Modules,
        int? Assets,
        int? Rentable,
        int? Users,
        int? Units,
        int Whatsapp);

    public static readonly CatalogRow[] CatalogRows =
    [
        new("reservas", "essencial", 9900, 99000, ["rentals"], null, 3, 3, 1, 0),
        new("reservas", "profissional", 18900, 189000, ["rentals"], null, 8, 10, 1, 500),
        new("reservas", "completo", 29900, 299000, ["rentals", "catalog"], null, 25, 30, 3, 2000),
        new("manutencao", "essencial", 9900, 99000, ["inventory", "pmoc", "os"], 150, null, 2, 1, 0),
        new("manutencao", "profissional", 18900, 189000, ["inventory", "pmoc", "os"], 800, null, 6, 1, 500),
        new("manutencao", "completo", 29900, 299000, ["inventory", "pmoc", "os", "resource-planning"], 3000, null, 15, 1, 2000),
        new("locacao", "essencial", 9900, 99000, ["rentals", "inventory", "catalog"], 50, null, 3, 1, 0),
        new("locacao", "profissional", 18900, 189000, ["rentals", "inventory", "catalog"], 300, null, 8, 1, 500),
        new("locacao", "completo", 29900, 299000, ["rentals", "inventory", "catalog"], 1500, null, 20, 3, 2000),
    ];

    public static string LimitsJson(int? assets, int? rentable, int? users, int? units, int whatsapp) =>
        JsonSerializer.Serialize(new Dictionary<string, int?>
        {
            [PlanLimitKeys.Assets] = assets,
            [PlanLimitKeys.RentableResources] = rentable,
            [PlanLimitKeys.Users] = users,
            [PlanLimitKeys.Units] = units,
            [PlanLimitKeys.WhatsappMessagesMonth] = whatsapp,
        });

    public static PlanCatalog Plan(
        string package,
        string plan,
        string limitsJson,
        int monthly = 18900,
        int annual = 189000,
        string[]? modules = null,
        bool active = true) =>
        new()
        {
            PackageKey = package,
            PlanKey = plan,
            Name = plan,
            MonthlyPriceCents = monthly,
            AnnualPriceCents = annual,
            ModulesJson = JsonSerializer.Serialize(modules ?? []),
            LimitsJson = limitsJson,
            IsActive = active,
            SortOrder = 1,
        };

    /// <summary>Adds the nine catalog rows to <paramref name="db"/> (not saved).</summary>
    public static IReadOnlyList<PlanCatalog> AddCatalog(AppDbContext db)
    {
        var plans = CatalogRows
            .Select((row, index) =>
            {
                var plan = Plan(
                    row.Package,
                    row.Plan,
                    LimitsJson(row.Assets, row.Rentable, row.Users, row.Units, row.Whatsapp),
                    row.Monthly,
                    row.Annual,
                    row.Modules);
                plan.SortOrder = index + 1;
                return plan;
            })
            .ToList();
        db.PlanCatalogs.AddRange(plans);
        return plans;
    }

    /// <summary>Adds a current subscription of <paramref name="tenant"/> on <paramref name="plan"/> (not saved).</summary>
    public static TenantSubscription Subscribe(
        AppDbContext db,
        Tenant tenant,
        PlanCatalog plan,
        string status = SubscriptionStatuses.Trialing,
        string billingMode = BillingModes.Provider,
        bool limitsUnlimited = false)
    {
        var subscription = new TenantSubscription
        {
            TenantId = tenant.Id,
            PlanCatalogId = plan.Id,
            Status = status,
            BillingMode = billingMode,
            LimitsUnlimited = limitsUnlimited,
            CurrentPeriodStart = DateTimeOffset.UtcNow,
        };
        db.TenantSubscriptions.Add(subscription);
        return subscription;
    }
}
