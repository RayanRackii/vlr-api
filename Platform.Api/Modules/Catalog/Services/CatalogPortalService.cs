using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Domain.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Catalog.Services;

public interface ICatalogPortalService
{
    Task<IReadOnlyList<PortalProductResponse>> ListProductsAsync(
        string? search,
        CancellationToken cancellationToken);

    Task<PortalProductResponse?> GetProductAsync(Guid id, CancellationToken cancellationToken);

    Task<CatalogOrderResponse> CreateOrderAsync(
        Guid customerId,
        CreatePortalOrderRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogOrderResponse>> ListOrdersAsync(
        Guid customerId,
        CancellationToken cancellationToken);

    Task<CatalogOrderResponse?> GetOrderAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken cancellationToken);

    Task<CatalogOrderResponse> CancelOrderAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken cancellationToken);

    Task<ProductRequestResponse> CreateProductRequestAsync(
        Guid customerId,
        CreateProductRequestDto request,
        IReadOnlyList<PortalUpload> files,
        CancellationToken cancellationToken);

    Task<CatalogFileUrlResponse> GetOwnProductRequestFileUrlAsync(
        Guid customerId,
        Guid requestId,
        Guid fileId,
        CancellationToken cancellationToken);
}

public sealed record PortalUpload(string FileName, string ContentType, byte[] Content);

public sealed class CatalogPortalService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions,
    ICatalogNotificationPublisher notificationPublisher,
    INotificationOutboxScheduler outboxScheduler) : ICatalogPortalService
{
    public async Task<IReadOnlyList<PortalProductResponse>> ListProductsAsync(
        string? search,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var products = dbContext.CatalogProducts
            .AsNoTracking()
            .Include(p => p.Files)
            .Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            products = products.Where(p =>
                p.Name.Contains(term) || (p.Code != null && p.Code.Contains(term)));
        }

        var list = await products.OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return list.Select(MapPortalProduct).ToList();
    }

    public async Task<PortalProductResponse?> GetProductAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await dbContext.CatalogProducts
            .AsNoTracking()
            .Include(p => p.Files)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsActive, cancellationToken);

        return product is null ? null : MapPortalProduct(product);
    }

    public async Task<CatalogOrderResponse> CreateOrderAsync(
        Guid customerId,
        CreatePortalOrderRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.");
        }

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Customer not found.");

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await dbContext.CatalogProducts
            .Where(p => productIds.Contains(p.Id) && p.IsActive)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            var orderNumber = await AllocateOrderNumberAsync(tenantId, cancellationToken);
            var order = new CatalogOrder
            {
                TenantId = tenantId,
                CustomerId = customer.Id,
                OrderNumber = orderNumber,
                CustomerNote = string.IsNullOrWhiteSpace(request.CustomerNote)
                    ? null
                    : request.CustomerNote.Trim(),
                CustomerNameSnapshot = customer.Name,
                CustomerEmailSnapshot = customer.Email,
                CustomerPhoneSnapshot = customer.Phone,
                Currency = "BRL",
            };

            decimal? total = 0m;
            var anyOnRequest = false;

            foreach (var line in request.Items)
            {
                if (line.Quantity <= 0)
                {
                    throw new ArgumentException("Quantity must be greater than zero.");
                }

                if (!products.TryGetValue(line.ProductId, out var product))
                {
                    throw new ArgumentException("One or more products are not available.");
                }

                decimal? unit = product.Price is null ? null : CatalogMoney.Round(product.Price.Value);
                decimal? subTotal = unit is null ? null : CatalogMoney.Round(unit.Value * line.Quantity);
                if (subTotal is null)
                {
                    anyOnRequest = true;
                }
                else
                {
                    total += subTotal;
                }

                order.AddItem(new CatalogOrderItem
                {
                    TenantId = tenantId,
                    OrderId = order.Id,
                    ProductId = product.Id,
                    ProductNameSnapshot = product.Name,
                    ProductCodeSnapshot = product.Code,
                    UnitPriceSnapshot = unit,
                    Currency = product.Currency,
                    Quantity = line.Quantity,
                    SubTotal = subTotal,
                });
            }

            order.TotalAmount = anyOnRequest ? null : CatalogMoney.Round(total ?? 0m);
            order.RecordHistory(
                CatalogOrderStatus.Requested,
                CatalogActorType.Customer,
                customer.Id,
                reason: null);

            dbContext.CatalogOrders.Add(order);

            var queued = await notificationPublisher.PublishOrderEventAsync(
                order,
                CatalogEventTypes.OrderCreated,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            foreach (var deliveryId in queued)
            {
                outboxScheduler.Schedule(deliveryId);
            }

            return CatalogOrderService.MapOrder(order);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<CatalogOrderResponse>> ListOrdersAsync(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var orders = await dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .Where(o => o.CustomerId == customerId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        return orders.Select(CatalogOrderService.MapOrder).ToList();
    }

    public async Task<CatalogOrderResponse?> GetOrderAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId, cancellationToken);

        return order is null ? null : CatalogOrderService.MapOrder(order);
    }

    public async Task<CatalogOrderResponse> CancelOrderAsync(
        Guid customerId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var order = await dbContext.CatalogOrders
            .Include(o => o.Items)
            .Include(o => o.History)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Order not found.");

        order.Cancel(CatalogActorType.Customer, reason: null, customerId);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidCatalogOrderTransitionException("The order was modified by another user.");
        }

        return CatalogOrderService.MapOrder(order);
    }

    public async Task<ProductRequestResponse> CreateProductRequestAsync(
        Guid customerId,
        CreateProductRequestDto request,
        IReadOnlyList<PortalUpload> files,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length == 0)
        {
            throw new ArgumentException("Description is required.");
        }

        if (request.Quantity <= 0)
        {
            throw new ArgumentException("Quantity must be greater than zero.");
        }

        _ = await dbContext.Customers.FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Customer not found.");

        var productRequest = new ProductRequest
        {
            TenantId = tenantId,
            CustomerId = customerId,
            Description = description,
            Quantity = request.Quantity,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };

        foreach (var upload in files)
        {
            CatalogFileRules.ValidateRequestUpload(
                upload.FileName,
                upload.ContentType,
                upload.Content.Length,
                upload.Content);

            var file = new ProductRequestFile
            {
                TenantId = tenantId,
                ProductRequestId = productRequest.Id,
                StorageKey = "pending",
                FileName = Path.GetFileName(upload.FileName),
                MimeType = upload.ContentType,
                SizeBytes = upload.Content.Length,
                Visibility = CatalogFileVisibility.InternalB2B,
            };

            var key = CatalogFileRules.StorageKey(tenantId, productRequest.Id, file.Id);
            await using var stream = new MemoryStream(upload.Content);
            await storageProvider.UploadAsync(
                storageOptions.Value.PrivateBucket,
                key,
                stream,
                upload.ContentType,
                cancellationToken);
            file.StorageKey = key;
            productRequest.AddFile(file);
        }

        dbContext.ProductRequests.Add(productRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
        return CatalogOrderService.MapProductRequest(productRequest);
    }

    public async Task<CatalogFileUrlResponse> GetOwnProductRequestFileUrlAsync(
        Guid customerId,
        Guid requestId,
        Guid fileId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var file = await dbContext.ProductRequestFiles
            .AsNoTracking()
            .Include(f => f.ProductRequest)
            .FirstOrDefaultAsync(
                f => f.Id == fileId
                     && f.ProductRequestId == requestId
                     && f.ProductRequest.CustomerId == customerId,
                cancellationToken)
            ?? throw new KeyNotFoundException("File not found.");

        var ttl = TimeSpan.FromSeconds(Math.Max(60, storageOptions.Value.SignedUrlTtlSeconds));
        var url = await storageProvider.CreateSignedUrlAsync(
            storageOptions.Value.PrivateBucket,
            file.StorageKey,
            ttl,
            cancellationToken);
        return new CatalogFileUrlResponse(url, IsPublic: false);
    }

    private async Task<int> AllocateOrderNumberAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var sequence = await dbContext.CatalogOrderNumberSequences
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (sequence is null)
        {
            sequence = new CatalogOrderNumberSequence { TenantId = tenantId, LastNumber = 0 };
            dbContext.CatalogOrderNumberSequences.Add(sequence);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(sequence).State = EntityState.Detached;
                sequence = await dbContext.CatalogOrderNumberSequences
                    .FirstAsync(s => s.TenantId == tenantId, cancellationToken);
            }
        }

        if (dbContext.Database.IsRelational())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM catalog.catalog_order_number_sequences WHERE tenant_id = {tenantId} FOR UPDATE",
                cancellationToken);
            await dbContext.Entry(sequence).ReloadAsync(cancellationToken);
        }

        sequence.LastNumber++;
        return sequence.LastNumber;
    }

    private PortalProductResponse MapPortalProduct(CatalogProduct product)
    {
        var files = product.Files
            .Where(f => f.Visibility == CatalogFileVisibility.CustomerVisible)
            .Select(f => new PortalProductFileDto(
                f.Id,
                f.FileName,
                f.MimeType,
                f.SizeBytes,
                storageProvider.GetPublicUrl(storageOptions.Value.PublicBucket, f.StorageKey)))
            .ToList();

        return new PortalProductResponse(
            product.Id,
            product.Name,
            product.Code,
            product.Description,
            product.Price,
            product.Currency,
            files);
    }

    private Guid EnsureTenant() =>
        tenantProvider.TenantId
        ?? throw new UnauthorizedAccessException("Tenant context is required.");
}
