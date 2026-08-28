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

        var list = await products
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return list.Select(p => Map(p, includeInternal: true)).ToList();
    }

    public async Task<CatalogProductResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .AsNoTracking()
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return product is null ? null : Map(product, includeInternal: true);
    }

    public async Task<CatalogProductResponse> CreateAsync(
        CreateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
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
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(product, includeInternal: true);
    }

    public async Task<CatalogProductResponse?> UpdateAsync(
        Guid id,
        UpdateCatalogProductRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        var code = NormalizeCode(request.Code);
        await EnsureCodeAvailableAsync(code, product.Id, cancellationToken);

        product.Name = NormalizeName(request.Name);
        product.Code = code;
        product.Description = NormalizeOptional(request.Description);
        product.Price = RoundPrice(request.Price);
        product.Currency = NormalizeCurrency(request.Currency);
        product.Touch();
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(product, includeInternal: true);
    }

    public async Task<CatalogProductResponse?> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        product.Deactivate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(product, includeInternal: true);
    }

    public async Task<CatalogProductResponse?> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return null;
        }

        product.Activate();
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(product, includeInternal: true);
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
        var product = await dbContext.CatalogProducts
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException("Product not found.");

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();
        CatalogFileRules.Validate(visibility, fileName, contentType, bytes.Length, bytes);

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
        var bucket = visibility == CatalogFileVisibility.CustomerVisible
            ? storageOptions.Value.PublicBucket
            : storageOptions.Value.PrivateBucket;

        buffer.Position = 0;
        await storageProvider.UploadAsync(bucket, key, buffer, contentType, cancellationToken);
        file.StorageKey = key;

        // Entity.Id is protected set; reconstruct via adding then setting storage after.
        product.AddFile(file);
        dbContext.CatalogProductFiles.Add(file);
        await dbContext.SaveChangesAsync(cancellationToken);

        var url = visibility == CatalogFileVisibility.CustomerVisible
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
        EnsureTenant();
        var file = await dbContext.CatalogProductFiles
            .FirstOrDefaultAsync(f => f.Id == fileId && f.ProductId == productId, cancellationToken)
            ?? throw new KeyNotFoundException("File not found.");

        var bucket = file.Visibility == CatalogFileVisibility.CustomerVisible
            ? storageOptions.Value.PublicBucket
            : storageOptions.Value.PrivateBucket;

        await storageProvider.DeleteAsync(bucket, file.StorageKey, cancellationToken);
        dbContext.CatalogProductFiles.Remove(file);
        await dbContext.SaveChangesAsync(cancellationToken);
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

        if (file.Visibility == CatalogFileVisibility.CustomerVisible)
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

    internal CatalogProductResponse Map(CatalogProduct product, bool includeInternal)
    {
        var files = product.Files
            .Where(f => includeInternal || f.Visibility == CatalogFileVisibility.CustomerVisible)
            .Select(f =>
            {
                string? url = null;
                if (f.Visibility == CatalogFileVisibility.CustomerVisible)
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
