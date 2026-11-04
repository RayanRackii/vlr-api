using Platform.Api.Modules.Taxonomy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogProductService
{
    Task<IReadOnlyList<CatalogProductResponse>> ListAsync(
        CatalogProductListQuery query,
        CancellationToken cancellationToken);

    Task<CatalogProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogProductResponse> CreateAsync(
        CreateCatalogProductRequest request,
        CancellationToken cancellationToken);

    Task<CatalogProductResponse?> UpdateAsync(
        Guid id,
        UpdateCatalogProductRequest request,
        CancellationToken cancellationToken);

    Task<CatalogProductResponse?> DeactivateAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogProductResponse?> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogProductFileDto> AddFileAsync(
        Guid productId,
        string fileName,
        string contentType,
        Stream content,
        CatalogFileVisibility visibility,
        CancellationToken cancellationToken);

    Task DeleteFileAsync(Guid productId, Guid fileId, CancellationToken cancellationToken);

    Task<CatalogFileUrlResponse> GetFileUrlAsync(
        Guid productId,
        Guid fileId,
        CancellationToken cancellationToken);
}

public sealed class CatalogProductService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions) : ICatalogProductService
{
    public async Task<IReadOnlyList<CatalogProductResponse>> ListAsync(
        CatalogProductListQuery query,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var products = dbContext.CatalogProducts
            .AsNoTracking()
            .Include(p => p.Files)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var name = query.Name.Trim();
            products = products.Where(p => p.Name.Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(query.Code))
        {
            var code = query.Code.Trim();
            products = products.Where(p => p.Code != null && p.Code.Contains(code));
        }

        if (query.IsActive is { } active)
        {
            products = products.Where(p => p.IsActive == active);
        }

        products = await CatalogTaxonomy.FilterAsync(dbContext, EnsureTenant(), products, query.Taxonomy, cancellationToken);

        var list = await products
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return await MapManyAsync(list, cancellationToken);
    }

    public async Task<CatalogProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .AsNoTracking()
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.TenantId == tenantProvider.TenantId && p.Id == id, cancellationToken);

        return product is null ? null : await MapOneAsync(product, cancellationToken);
    }

    public async Task<CatalogProductResponse> CreateAsync(
        CreateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(dbContext, tenantId, cancellationToken);
        await CatalogTaxonomy.ValidateAsync(dbContext, tenantId, request.Taxonomy, cancellationToken);
        var name = NormalizeName(request.Name);
        var code = NormalizeCode(request.Code);
        var isActive = NormalizeStatus(request.Status);
        await EnsureCodeAvailableAsync(code, excludeId: null, cancellationToken);

        var product = new CatalogProduct
        {
            TenantId = tenantId,
            Name = name,
            Code = code,
            Description = NormalizeOptional(request.Description),
            Price = RoundPrice(request.Price),
            Currency = NormalizeCurrency(request.Currency),
            IsActive = isActive,
        };

        dbContext.CatalogProducts.Add(product);
        await CatalogTaxonomy.ReplaceAsync(dbContext, tenantId, product.Id, request.Taxonomy, cancellationToken);
        await TaxonomyService.SaveAsync(dbContext, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await MapOneAsync(product, cancellationToken);
    }

    public async Task<CatalogProductResponse?> UpdateAsync(
        Guid id,
        UpdateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        await using var scope = await TaxonomyMutationScope.BeginAsync(dbContext, tenantId, cancellationToken);
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        await CatalogTaxonomy.ValidateAsync(dbContext, tenantId, request.Taxonomy, cancellationToken);
        var name = NormalizeName(request.Name);
        var price = RoundPrice(request.Price);
        var currency = NormalizeCurrency(request.Currency);
        var code = NormalizeCode(request.Code);
        await EnsureCodeAvailableAsync(code, product.Id, cancellationToken);

        product.Name = name;
        product.Code = code;
        product.Description = NormalizeOptional(request.Description);
        product.Price = price;
        product.Currency = currency;
        product.Touch();
        await CatalogTaxonomy.ReplaceAsync(dbContext, tenantId, product.Id, request.Taxonomy, cancellationToken);
        await TaxonomyService.SaveAsync(dbContext, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return await MapOneAsync(product, cancellationToken);
    }

    public async Task<CatalogProductResponse?> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.TenantId == tenantProvider.TenantId && p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        product.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapOneAsync(product, cancellationToken);
    }

    public async Task<CatalogProductResponse?> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.TenantId == tenantProvider.TenantId && p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        product.Activate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapOneAsync(product, cancellationToken);
    }

    public async Task<CatalogProductFileDto> AddFileAsync(
        Guid productId,
        string fileName,
        string contentType,
        Stream content,
        CatalogFileVisibility visibility,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        CatalogFileRules.Validate(visibility, fileName, contentType, bytes.Length, bytes);

        CatalogProduct product = null!;
        CatalogProductFile? file = null;
        string bucket = null!;
        try
        {
            // Commit the reservation BEFORE touching storage. Its presence blocks visibility changes
            // even if the process dies during upload or the final Ready commit is not acknowledged.
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;
            await LockProductAsync(tenantId, productId, cancellationToken);
            product = await dbContext.CatalogProducts
                .Include(p => p.Files)
                .FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == productId, cancellationToken)
                ?? throw new KeyNotFoundException("Product not found.");
            if (transaction is not null) await dbContext.Entry(product).ReloadAsync(cancellationToken);

            file = new CatalogProductFile
            {
                TenantId = tenantId,
                ProductId = product.Id,
                StorageKey = "pending",
                FileName = Path.GetFileName(fileName),
                MimeType = contentType,
                SizeBytes = bytes.Length,
                Visibility = visibility,
                Status = CatalogFileStatus.Pending,
            };
            file.StorageKey = CatalogFileRules.StorageKey(tenantId, product.Id, file.Id);
            bucket = CatalogFileBuckets.For(storageOptions.Value, product.Visibility, visibility);
            product.AddFile(file);
            dbContext.CatalogProductFiles.Add(file);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // Reconcile a lost acknowledgement, or retry a failed reservation once without the
            // request cancellation. Never upload if this phase fails, even when recovery succeeds.
            if (file is not null) await RecoverPendingFileAsync(file);
            throw;
        }

        try
        {
            // Committing Pending released the row lock. Reacquire it and verify the reservation:
            // a concurrent explicit delete may have removed it in between these two transactions.
            await using var transaction = dbContext.Database.IsRelational()
                ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;
            await LockProductAsync(tenantId, productId, cancellationToken);
            await dbContext.Entry(file).ReloadAsync(cancellationToken);
            if (dbContext.Entry(file).State == EntityState.Detached)
                throw new KeyNotFoundException("File reservation was deleted.");

            buffer.Position = 0;
            await storageProvider.UploadAsync(bucket, file.StorageKey, buffer, contentType, cancellationToken);
            file.Status = CatalogFileStatus.Ready;
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The transaction has been disposed before this read. A failed final save/commit can
            // mean Pending OR Ready durably; reconcile from the database and never delete storage.
            await dbContext.Entry(file).ReloadAsync(CancellationToken.None);
            throw;
        }

        var url = CatalogFileBuckets.IsPublic(product.Visibility, visibility)
            ? storageProvider.GetPublicUrl(bucket, file.StorageKey)
            : null;

        return new CatalogProductFileDto(
            file.Id,
            file.FileName,
            file.MimeType,
            file.SizeBytes,
            file.Visibility,
            url,
            file.Status);
    }

    public async Task DeleteFileAsync(
        Guid productId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        // Same product-row lock as a visibility change: held until commit, visibility read after it,
        // so a delete cannot race an upload or visibility edit.
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenantId} AND id = {productId} FOR UPDATE", cancellationToken);
        }

        var file = await dbContext.CatalogProductFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProductId == productId, cancellationToken)
            ?? throw new KeyNotFoundException("File not found.");

        var productVisibility = await ProductVisibilityAsync(productId, cancellationToken);
        var bucket = CatalogFileBuckets.For(storageOptions.Value, productVisibility, file.Visibility);

        // Storage deletion is idempotent (missing is success). Keep the blocking row until
        // deletion succeeds, including for Pending/Failed uploads with an uncertain object state.
        await storageProvider.DeleteRequiredAsync(bucket, file.StorageKey, cancellationToken);
        dbContext.CatalogProductFiles.Remove(file);
        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(CancellationToken.None);
    }

    public async Task<CatalogFileUrlResponse> GetFileUrlAsync(
        Guid productId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var file = await dbContext.CatalogProductFiles
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProductId == productId && f.Status == CatalogFileStatus.Ready, cancellationToken)
            ?? throw new KeyNotFoundException("File not found.");

        var productVisibility = await ProductVisibilityAsync(productId, cancellationToken);
        if (CatalogFileBuckets.IsPublic(productVisibility, file.Visibility))
        {
            var publicUrl = storageProvider.GetPublicUrl(
                storageOptions.Value.PublicBucket,
                file.StorageKey);
            return new CatalogFileUrlResponse(publicUrl, IsPublic: true);
        }

        var ttl = TimeSpan.FromSeconds(Math.Max(60, storageOptions.Value.SignedUrlTtlSeconds));
        var signed = await storageProvider.CreateSignedUrlAsync(
            storageOptions.Value.PrivateBucket,
            file.StorageKey,
            ttl,
            cancellationToken);
        return new CatalogFileUrlResponse(signed, IsPublic: false);
    }

    private async Task LockProductAsync(Guid tenantId, Guid productId, CancellationToken ct)
    {
        if (dbContext.Database.IsRelational())
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenantId} AND id = {productId} FOR UPDATE", ct);
    }

    private async Task RecoverPendingFileAsync(CatalogProductFile file)
    {
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(CancellationToken.None)
            : null;
        await LockProductAsync(file.TenantId, file.ProductId, CancellationToken.None);
        var exists = await dbContext.CatalogProductFiles.AsNoTracking()
            .AnyAsync(f => f.Id == file.Id, CancellationToken.None);
        if (exists)
            await dbContext.Entry(file).ReloadAsync(CancellationToken.None);
        else
        {
            file.Status = CatalogFileStatus.Pending;
            dbContext.Entry(file).State = EntityState.Added;
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        if (transaction is not null) await transaction.CommitAsync(CancellationToken.None);
    }

    private Task<CatalogProductVisibility> ProductVisibilityAsync(Guid productId, CancellationToken ct) =>
        dbContext.CatalogProducts.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => p.Visibility)
            .FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<CatalogProductResponse>> MapManyAsync(IReadOnlyList<CatalogProduct> products, CancellationToken ct)
    {
        var tenantId = EnsureTenant();
        var assignments = await CatalogTaxonomy.LoadAsync(dbContext, tenantId, products.Select(x => x.Id), ct);
        var ids = products.Select(x => x.Id).ToList();
        var grants = await dbContext.CatalogProductAccess.AsNoTracking()
            .Where(a => a.TenantId == tenantId && ids.Contains(a.ProductId))
            .GroupBy(a => a.ProductId)
            .Select(g => new { g.Key, Customers = g.Count(a => a.CustomerId != null), Groups = g.Count(a => a.GroupId != null) })
            .ToDictionaryAsync(x => x.Key, ct);
        return products.Select(p => Map(p, true) with
        {
            Taxonomy = assignments[p.Id],
            Visibility = p.Visibility,
            GrantedCustomerCount = grants.TryGetValue(p.Id, out var g) ? g.Customers : 0,
            GrantedGroupCount = grants.TryGetValue(p.Id, out var g2) ? g2.Groups : 0,
        }).ToArray();
    }

    private async Task<CatalogProductResponse> MapOneAsync(CatalogProduct product, CancellationToken ct) =>
        (await MapManyAsync([product], ct))[0];

    internal CatalogProductResponse Map(CatalogProduct product, bool includeInternal)
    {
        var files = product.Files
            .Where(f => includeInternal || (f.Visibility == CatalogFileVisibility.CustomerVisible && f.Status == CatalogFileStatus.Ready))
            .Select(f =>
            {
                string? url = null;
                // Restricted products keep files private: staff fetch them via GetFileUrlAsync (signed).
                if (f.Status == CatalogFileStatus.Ready && CatalogFileBuckets.IsPublic(product.Visibility, f.Visibility))
                {
                    url = storageProvider.GetPublicUrl(storageOptions.Value.PublicBucket, f.StorageKey);
                }

                return new CatalogProductFileDto(
                    f.Id,
                    f.FileName,
                    f.MimeType,
                    f.SizeBytes,
                    f.Visibility,
                    url,
                    f.Status);
            })
            .ToList();

        return new CatalogProductResponse(
            product.Id,
            product.Name,
            product.Code,
            product.Description,
            product.Price,
            product.Currency,
            product.IsActive,
            product.CreatedAt,
            files);
    }

    private async Task EnsureCodeAvailableAsync(
        string? code,
        Guid? excludeId,
        CancellationToken cancellationToken)
    {
        if (code is null)
        {
            return;
        }

        var taken = await dbContext.CatalogProducts
            .AnyAsync(
                p => p.Code == code && (excludeId == null || p.Id != excludeId),
                cancellationToken);

        if (taken)
        {
            throw new InvalidOperationException("A product with this code already exists.");
        }
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length is < 1 or > 200)
        {
            throw new ArgumentException("Name is required.");
        }

        return trimmed;
    }

    /// <summary>Maps the optional create status to IsActive. Absent defaults to Active so the
    /// default stays unchanged; "Inactive" creates unpublished so create-inactive never publishes.</summary>
    private static bool NormalizeStatus(string? status)
    {
        if (status is null) return true;
        return status.Trim().ToLowerInvariant() switch
        {
            "active" => true,
            "inactive" => false,
            _ => throw new ArgumentException("Status must be Active or Inactive."),
        };
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var trimmed = code.Trim();
        return trimmed.Length > 80
            ? throw new ArgumentException("Code is too long.")
            : trimmed;
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static decimal? RoundPrice(decimal? price)
    {
        if (price is null)
        {
            return null;
        }

        if (price < 0)
        {
            throw new ArgumentException("Price cannot be negative.");
        }

        return CatalogMoney.Round(price.Value);
    }

    private static string NormalizeCurrency(string? currency)
    {
        var value = string.IsNullOrWhiteSpace(currency) ? "BRL" : currency.Trim().ToUpperInvariant();
        if (value.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-letter ISO code.");
        }

        return value;
    }
}
