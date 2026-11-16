using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Raised when staff tries to activate (or create as active) a product that cannot be sold yet.
/// The controller maps it to 409 <c>{ code: "PRODUCT_NOT_READY", missing: [...] }</c>.
/// </summary>
public sealed class ProductNotReadyException(IReadOnlyList<string> missing)
    : InvalidOperationException("Product is not ready for sale: " + string.Join(", ", missing))
{
    public const string Code = "PRODUCT_NOT_READY";

    public IReadOnlyList<string> Missing { get; } = missing;
}

/// <summary>
/// The single definition of "this product can be sold in the portal".
/// <list type="bullet">
/// <item><c>NAME</c>: the name is not blank.</item>
/// <item><c>RESOURCE</c>: a scheduled product (<see cref="CatalogProduct.UsesSchedule"/>) needs at least one active
/// requirement with a rental asset, but only while the tenant's rentals module is active.</item>
/// <item><c>AUDIENCE</c>: a Restricted product needs at least one grant (a customer, or a group with a member).</item>
/// <item><c>PRICE</c>: a null price is valid ("Sob consulta"); a price that is set must be greater than zero.</item>
/// </list>
/// <see cref="IsSellable"/> is the EF predicate; <see cref="Missing"/> is the per-product check. Keep them in sync
/// (a parity test enforces it).
/// </summary>
public static class CatalogProductReadiness
{
    public const string Name = "NAME";
    public const string Resource = "RESOURCE";
    public const string Audience = "AUDIENCE";
    public const string Price = "PRICE";

    /// <summary>True when the tenant has the rentals module active (resource requirements are enforced only then).</summary>
    public static async Task<bool> IsRentalsActiveAsync(
        AppDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        var names = await dbContext.TenantModules
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.TenantId == tenantId && m.IsActive)
            .Select(m => m.ModuleName)
            .ToListAsync(cancellationToken);
        return names.Any(n => string.Equals(n.Trim(), PlatformModules.Rentals, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>EF-translatable predicate: products that can be sold. Use in portal queries.</summary>
    public static Expression<Func<CatalogProduct, bool>> IsSellable(
        AppDbContext dbContext, Guid tenantId, bool rentalsActive) =>
        p => p.Name.Trim() != ""
            && (p.Price == null || p.Price > 0)
            && (!rentalsActive
                || !p.UsesSchedule
                || dbContext.CatalogProductResourceRequirements.Any(r =>
                    r.TenantId == tenantId && r.ProductId == p.Id && r.IsActive && r.RentalAssetId != null))
            && (p.Visibility != CatalogProductVisibility.Restricted
                || dbContext.CatalogProductAccess.Any(a =>
                    a.TenantId == tenantId
                    && a.ProductId == p.Id
                    && (a.CustomerId != null
                        || (a.GroupId != null && dbContext.CustomerGroupMembers.Any(m =>
                            m.TenantId == tenantId && m.GroupId == a.GroupId)))));

    /// <summary>Per-product check over already-known facts. Returns the missing codes (empty when ready).</summary>
    public static IReadOnlyList<string> Missing(
        CatalogProduct product, bool hasResource, bool hasAudience, bool rentalsActive)
    {
        var missing = new List<string>(4);
        if (string.IsNullOrWhiteSpace(product.Name)) missing.Add(Name);
        if (rentalsActive && product.UsesSchedule && !hasResource) missing.Add(Resource);
        if (product.Visibility == CatalogProductVisibility.Restricted && !hasAudience) missing.Add(Audience);
        if (product.Price is { } price && price <= 0) missing.Add(Price);
        return missing;
    }

    /// <summary>Loads the resource/audience facts for the given persisted products and evaluates each one.</summary>
    public static async Task<Dictionary<Guid, IReadOnlyList<string>>> MissingForAsync(
        AppDbContext dbContext,
        Guid tenantId,
        IReadOnlyCollection<CatalogProduct> products,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        if (products.Count == 0) return result;

        var rentalsActive = await IsRentalsActiveAsync(dbContext, tenantId, cancellationToken);
        var ids = products.Select(p => p.Id).ToList();

        HashSet<Guid> withResource = [];
        if (rentalsActive)
        {
            withResource = (await dbContext.CatalogProductResourceRequirements.AsNoTracking()
                .Where(r => r.TenantId == tenantId && ids.Contains(r.ProductId) && r.IsActive && r.RentalAssetId != null)
                .Select(r => r.ProductId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        var restrictedIds = products
            .Where(p => p.Visibility == CatalogProductVisibility.Restricted)
            .Select(p => p.Id)
            .ToList();
        HashSet<Guid> withAudience = [];
        if (restrictedIds.Count > 0)
        {
            withAudience = (await dbContext.CatalogProductAccess.AsNoTracking()
                .Where(a => a.TenantId == tenantId
                    && restrictedIds.Contains(a.ProductId)
                    && (a.CustomerId != null
                        || (a.GroupId != null && dbContext.CustomerGroupMembers.Any(m =>
                            m.TenantId == tenantId && m.GroupId == a.GroupId))))
                .Select(a => a.ProductId)
                .Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        foreach (var p in products)
        {
            result[p.Id] = Missing(p, withResource.Contains(p.Id), withAudience.Contains(p.Id), rentalsActive);
        }

        return result;
    }
}
