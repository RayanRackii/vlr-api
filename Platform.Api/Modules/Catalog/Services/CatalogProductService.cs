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
        await EnsureCodeAvailableAsync(code, excludeId: null, cancellationToken);

        var product = new CatalogProduct
        {
            TenantId = tenantId,
            Name = name,
            Code = code,
            Description = NormalizeOptional(request.Description),
            Price = RoundPrice(request.Price),
            Currency = NormalizeCurrency(request.Currency),
            IsActive = true,
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

        // Take the same product-row lock as a visibility change so an upload cannot race a
        // restriction: the lock is held until commit and the product visibility is read AFTER it,
        // guaranteeing the chosen bucket matches where a concurrent move would place the file.
        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {tenantId} AND id = {productId} FOR UPDATE", cancellationToken);
        }

        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException("Product not found.");

        var file = new CatalogProductFile
        {
            TenantId = tenantId,
            ProductId = product.Id,
            StorageKey = "pending",
            FileName = Path.GetFileName(fileName),
            MimeType = contentType,
            SizeBytes = bytes.Length,
            Visibility = visibility,
        };

        var key = CatalogFileRules.StorageKey(tenantId, product.Id, file.Id);
        var bucket = CatalogFileBuckets.For(storageOptions.Value, product.Visibility, visibility);

        buffer.Position = 0;
        try
        {
            await storageProvider.UploadAsync(bucket, key, buffer, contentType, cancellationToken);
            file.StorageKey = key;

            // Entity.Id is protected set; reconstruct via adding then setting storage after.
            product.AddFile(file);
            dbContext.CatalogProductFiles.Add(file);
            await dbContext.SaveChangesAsync(cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // The object was uploaded but the row did not commit: remove the orphan, best effort.
            try { await storageProvider.DeleteAsync(bucket, key, CancellationToken.None); }
            catch (Exception) { /* best effort; provider logs failures */ }
            throw;
        }

        var url = CatalogFileBuckets.IsPublic(product.Visibility, visibility)
            ? storageProvider.GetPublicUrl(bucket, key)
            : null;

        return new CatalogProductFileDto(
            file.Id,
            file.FileName,
            file.MimeType,
            file.SizeBytes,
            file.Visibility,
            url);
    }

    public async Task DeleteFileAsync(
        Guid productId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();

        // Same product-row lock as a visibility change: held until commit, visibility read after it,
        // so a delete cannot race a restriction that moves files between buckets.
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

        // Remove the row (uncommitted), then require the object delete, then commit. Committing only
        // after the object is gone means a failure (including cancellation) rolls back so the row
        // survives, leaving the object still referenced rather than orphaned and undiscoverable.
        dbContext.CatalogProductFiles.Remove(file);
        await dbContext.SaveChangesAsync(cancellationToken);
        try
        {
            await storageProvider.DeleteRequiredAsync(bucket, file.StorageKey, cancellationToken);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
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
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProductId == productId, cancellationToken)
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
            .Where(f => includeInternal || f.Visibility == CatalogFileVisibility.CustomerVisible)
            .Select(f =>
            {
                string? url = null;
                // Restricted products keep files private: staff fetch them via GetFileUrlAsync (signed).
                if (CatalogFileBuckets.IsPublic(product.Visibility, f.Visibility))
                {
                    url = storageProvider.GetPublicUrl(storageOptions.Value.PublicBucket, f.StorageKey);
                }

                return new CatalogProductFileDto(
                    f.Id,
                    f.FileName,
                    f.MimeType,
                    f.SizeBytes,
                    f.Visibility,
                    url);
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
