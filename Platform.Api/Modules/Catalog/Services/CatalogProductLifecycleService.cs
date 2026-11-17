using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Taxonomy.Services;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

/// <summary>
/// Raised when a product cannot be deleted because catalog orders reference it (orders must keep
/// their product). The controller maps it to 409 <c>{ code: "PRODUCT_HAS_ORDERS" }</c>; the client
/// then suggests deactivating the product instead.
/// </summary>
public sealed class ProductHasOrdersException()
    : InvalidOperationException("Product has orders and cannot be deleted; deactivate it instead.")
{
    public const string Code = "PRODUCT_HAS_ORDERS";
}

public interface ICatalogProductLifecycleService
{
    /// <summary>
    /// Creates an inactive copy of the product and returns the new product id, or null when the
    /// source product does not exist in the current tenant. Files are NOT copied (storage objects).
    /// </summary>
    Task<Guid?> DuplicateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the product and its dependants in one transaction. Returns false when the product does
    /// not exist in the current tenant; throws <see cref="ProductHasOrdersException"/> when any order
    /// item references it.
    /// </summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class CatalogProductLifecycleService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions,
    ILogger<CatalogProductLifecycleService> logger,
    IProductHistoryRecorder? historyRecorder = null) : ICatalogProductLifecycleService
{
    private readonly IProductHistoryRecorder _history = historyRecorder ?? new CatalogProductHistoryService(dbContext, tenantProvider);
    private const int MaxNameLength = 200;
    private const int MaxCodeLength = 80;
    private const string NamePrefix = "Cópia de ";
    private const string CodeSuffix = "-COPIA";

    public async Task<Guid?> DuplicateAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        // Serializes duplicates (and taxonomy edits) per tenant: the code uniqueness probe below
        // cannot race another duplicate, and copied taxonomy links cannot race a taxonomy delete.
        await using var scope = await TaxonomyMutationScope.BeginAsync(dbContext, tenantId, cancellationToken);

        var source = await dbContext.CatalogProducts
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);
        if (source is null) return null;

        var copy = new CatalogProduct
        {
            TenantId = tenantId,
            Name = CopyName(source.Name),
            Code = await CopyCodeAsync(tenantId, source.Code, cancellationToken),
            Description = source.Description,
            Price = source.Price,
            Currency = source.Currency,
            UsesSchedule = source.UsesSchedule,
            Visibility = source.Visibility,
            // A copy is never published automatically: the owner reviews it and activates it.
            IsActive = false,
        };
        dbContext.CatalogProducts.Add(copy);

        var categories = await dbContext.CatalogProductCategories.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == id)
            .ToListAsync(cancellationToken);
        foreach (var link in categories)
            dbContext.CatalogProductCategories.Add(new() { TenantId = tenantId, ProductId = copy.Id, CategoryId = link.CategoryId });

        var tags = await dbContext.CatalogProductTags.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == id)
            .ToListAsync(cancellationToken);
        foreach (var link in tags)
            dbContext.CatalogProductTags.Add(new() { TenantId = tenantId, ProductId = copy.Id, TagId = link.TagId });

        var grants = await dbContext.CatalogProductAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ProductId == id)
            .ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            dbContext.CatalogProductAccess.Add(new CatalogProductAccess
            {
                TenantId = tenantId,
                ProductId = copy.Id,
                CustomerId = grant.CustomerId,
                GroupId = grant.GroupId,
            });
        }

        // Only the active requirement set is configuration; inactive rows are history for old orders.
        var requirements = await dbContext.CatalogProductResourceRequirements.AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.ProductId == id && r.IsActive)
            .ToListAsync(cancellationToken);
        if (requirements.Count > 0)
        {
            copy.ResourceRequirementsVersion = 1;
            foreach (var requirement in requirements)
            {
                dbContext.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
                {
                    TenantId = tenantId,
                    ProductId = copy.Id,
                    RentalAssetId = requirement.RentalAssetId,
                    AssetCategoryId = requirement.AssetCategoryId,
                    ResourceQuantity = requirement.ResourceQuantity,
                    DurationMinutesPerUnit = requirement.DurationMinutesPerUnit,
                    PrepMinutes = requirement.PrepMinutes,
                    CleanupMinutes = requirement.CleanupMinutes,
                    IsActive = true,
                    Version = copy.ResourceRequirementsVersion,
                });
            }
        }

        await _history.RecordAsync(source.Id, CatalogProductHistoryActions.Duplicated,
            [new CatalogProductHistoryChange("copy", null, copy.Id.ToString())], cancellationToken);
        await _history.RecordAsync(copy.Id, CatalogProductHistoryActions.Duplicated,
            [new CatalogProductHistoryChange("source", source.Name, null)], cancellationToken);
        await TaxonomyService.SaveAsync(dbContext, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return copy.Id;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        // Same product-row lock as file upload/delete: held until commit. Creating an order item takes
        // a key-share lock on the product row, so the order check below cannot race a new order.
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenantId} AND id = {id} FOR UPDATE",
                cancellationToken);
        }

        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);
        if (product is null) return false;

        if (await dbContext.CatalogOrderItems.AnyAsync(i => i.TenantId == tenantId && i.ProductId == id, cancellationToken))
            throw new ProductHasOrdersException();

        // Collected before the rows go away; the objects are removed only after the commit.
        var files = product.Files.ToList();
        var storedObjects = files
            .Select(f => (Bucket: CatalogFileBuckets.For(storageOptions.Value, f), Key: f.StorageKey))
            .ToList();

        dbContext.CatalogProductFiles.RemoveRange(files);
        dbContext.CatalogProductAccess.RemoveRange(await dbContext.CatalogProductAccess
            .Where(a => a.TenantId == tenantId && a.ProductId == id).ToListAsync(cancellationToken));
        dbContext.CatalogProductCategories.RemoveRange(await dbContext.CatalogProductCategories
            .Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken));
        dbContext.CatalogProductTags.RemoveRange(await dbContext.CatalogProductTags
            .Where(x => x.TenantId == tenantId && x.ProductId == id).ToListAsync(cancellationToken));
        // All versions, inactive included: no order item exists, so nothing references them.
        dbContext.CatalogProductResourceRequirements.RemoveRange(await dbContext.CatalogProductResourceRequirements
            .Where(r => r.TenantId == tenantId && r.ProductId == id).ToListAsync(cancellationToken));
        dbContext.CatalogProducts.Remove(product);

        // Keyed by product id with no FK, so the audit row survives the product. Same SaveChanges.
        await _history.RecordAsync(id, CatalogProductHistoryActions.Deleted,
            [new CatalogProductHistoryChange("name", product.Name, null)], cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        // Best-effort: an orphan object is harmless, a missing image behind a live row is not, so a
        // storage failure never fails the request. Pending/Failed uploads are included on purpose.
        foreach (var (bucket, key) in storedObjects)
        {
            try
            {
                await storageProvider.DeleteRequiredAsync(bucket, key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not delete storage object of deleted catalog product. Tenant {TenantId}, product {ProductId}, key {StorageKey}.",
                    tenantId,
                    id,
                    key);
            }
        }

        return true;
    }

    private static string CopyName(string name)
    {
        var copy = NamePrefix + name;
        return copy.Length <= MaxNameLength ? copy : copy[..MaxNameLength].TrimEnd();
    }

    private async Task<string?> CopyCodeAsync(Guid tenantId, string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;

        for (var n = 1; n <= 1000; n++)
        {
            var suffix = n == 1 ? CodeSuffix : CodeSuffix + n;
            var candidate = code[..Math.Min(code.Length, MaxCodeLength - suffix.Length)] + suffix;
            var taken = await dbContext.CatalogProducts
                .AnyAsync(p => p.TenantId == tenantId && p.Code == candidate, ct);
            if (!taken) return candidate;
        }

        throw new InvalidOperationException("Could not find a free code for the copy.");
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
