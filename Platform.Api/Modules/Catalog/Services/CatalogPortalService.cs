using Platform.Api.DomainEvents;
using Platform.Api.Modules.Taxonomy.Dtos;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Common;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Contracts;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Core.Domain.Events;
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
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken,
        TaxonomyFilter? taxonomy = null);

    Task<PortalProductResponse?> GetProductAsync(Guid? customerId, Guid id, CancellationToken cancellationToken);

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

    Task<ProductRequestResponse> CancelProductRequestAsync(
        Guid customerId, Guid requestId, CancellationToken cancellationToken);

    Task<ProductRequestResponse> CreateProductRequestAsync(
        Guid customerId,
        CreateProductRequestDto request,
        IReadOnlyList<PortalUpload> files,
        CancellationToken cancellationToken);

    Task<PagedResult<PortalProductRequestResponse>> ListOwnProductRequestsAsync(
        Guid customerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<PortalProductRequestResponse?> GetOwnProductRequestAsync(
        Guid customerId,
        Guid requestId,
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
    IDomainEventDispatcher<CatalogOrderEvent> domainEvents,
    INotificationOutboxScheduler outboxScheduler,
    IResourceOccupancyService occupancy) : ICatalogPortalService
{
    public async Task<IReadOnlyList<PortalProductResponse>> ListProductsAsync(
        Guid? customerId,
        string? search,
        CancellationToken cancellationToken,
        TaxonomyFilter? taxonomy = null)
    {
        EnsureTenant();
        var products = ApplyVisibility(
            dbContext.CatalogProducts
                .AsNoTracking()
                .Include(p => p.Files)
                .Where(p => p.IsActive),
            customerId);
        products = await CatalogTaxonomy.FilterAsync(dbContext, EnsureTenant(), products, taxonomy, cancellationToken);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var hasUnaccent = dbContext.Database.IsNpgsql()
                && await dbContext.Database.SqlQueryRaw<bool>(
                    """
                    SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'unaccent')
                        AND to_regprocedure('unaccent(text)') IS NOT NULL AS "Value"
                    """).SingleAsync(cancellationToken);

            if (hasUnaccent)
            {
                var pattern = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                products = products.Where(p =>
                    EF.Functions.ILike(EF.Functions.Unaccent(p.Name), EF.Functions.Unaccent(pattern), "\\")
                    || (p.Code != null && EF.Functions.ILike(
                        EF.Functions.Unaccent(p.Code), EF.Functions.Unaccent(pattern), "\\")));
            }
            else
            {
                // Keep tenant/active filters and ordering in SQL; no extension installation is required.
                var candidates = await products.OrderBy(p => p.Name).ToListAsync(cancellationToken);
                const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
                var matches = candidates.Where(p =>
                        CultureInfo.InvariantCulture.CompareInfo.IndexOf(p.Name, term, options) >= 0
                        || (p.Code != null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(p.Code, term, options) >= 0))
                    .ToList();
                return await MapProductsAsync(matches, cancellationToken);
            }
        }

        var list = await products.OrderBy(p => p.Name).ToListAsync(cancellationToken);
        return await MapProductsAsync(list, cancellationToken);
    }

    public async Task<PortalProductResponse?> GetProductAsync(
        Guid? customerId,
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var product = await ApplyVisibility(
                dbContext.CatalogProducts
                    .AsNoTracking()
                    .Include(p => p.Files)
                    .Where(p => p.TenantId == tenantProvider.TenantId && p.Id == id && p.IsActive),
                customerId)
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? null : (await MapProductsAsync([product], cancellationToken))[0];
    }

    /// <summary>
    /// Restricts a product query to what <paramref name="customerId"/> may see: every
    /// <see cref="CatalogProductVisibility.Public"/> product, plus any
    /// <see cref="CatalogProductVisibility.Restricted"/> product granted to the customer directly or
    /// through one of their groups. A null customer (anonymous visitor) sees only public products.
    /// Applied on the IQueryable before search/pagination so it never leaks through any branch.
    /// </summary>
    private IQueryable<CatalogProduct> ApplyVisibility(IQueryable<CatalogProduct> products, Guid? customerId)
    {
        if (customerId is null)
        {
            return products.Where(p => p.Visibility == CatalogProductVisibility.Public);
        }

        var id = customerId.Value;
        var tenantId = EnsureTenant();
        return products.Where(p =>
            p.Visibility == CatalogProductVisibility.Public
            || dbContext.CatalogProductAccess.Any(a =>
                a.TenantId == tenantId
                && a.ProductId == p.Id
                && (a.CustomerId == id
                    || (a.GroupId != null && dbContext.CustomerGroupMembers.Any(m =>
                        m.TenantId == tenantId && m.GroupId == a.GroupId && m.CustomerId == id)))));
    }

    public async Task<CatalogOrderResponse> CreateOrderAsync(
        Guid customerId,
        CreatePortalOrderRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateCustomerNote(request.CustomerNote);
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("At least one order item is required.");
        }

        var customer = await dbContext.Customers
            .FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Customer not found.");

        var productIds = request.Items.Select(i => i.ProductId).Distinct().ToList();
        // Re-check visibility server-side: a Restricted product the customer may not see is simply
        // absent from this dictionary, so the loop below treats it as an unavailable product (the same
        // path a non-existent id takes today) and never leaks that the product exists.
        var products = await ApplyVisibility(
                dbContext.CatalogProducts.Where(p => productIds.Contains(p.Id) && p.IsActive),
                customerId)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        // Snapshot each product's currently-active resource requirement rows onto the order item at
        // creation time (order-equipment-link-codex §1: "Capture when the order is created. Product
        // edits affect subsequent orders only."). Products with no active requirement rows behave as
        // ordinary (non-scheduled) products — nothing is snapshotted for them.
        var activeRequirementsByProduct = await dbContext.CatalogProductResourceRequirements
            .Where(r => productIds.Contains(r.ProductId) && r.IsActive)
            .ToListAsync(cancellationToken);
        var requirementsByProduct = activeRequirementsByProduct
            .GroupBy(r => r.ProductId)
            .ToDictionary(g => g.Key, g => g.ToList());

        await domainEvents.EnsureReadyAsync(cancellationToken);

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

                var orderItem = new CatalogOrderItem
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
                };
                order.AddItem(orderItem);
                dbContext.CatalogOrderItems.Add(orderItem);

                if (requirementsByProduct.TryGetValue(product.Id, out var activeRequirements))
                {
                    foreach (var requirement in activeRequirements)
                    {
                        dbContext.CatalogOrderItemResourceRequirements.Add(new CatalogOrderItemResourceRequirement
                        {
                            TenantId = tenantId,
                            OrderItemId = orderItem.Id,
                            SourceRequirementId = requirement.Id,
                            RentalAssetId = requirement.RentalAssetId,
                            AssetCategoryId = requirement.AssetCategoryId,
                            ResourceQuantity = requirement.ResourceQuantity,
                            DurationMinutesPerUnit = requirement.DurationMinutesPerUnit,
                            PrepMinutes = requirement.PrepMinutes,
                            CleanupMinutes = requirement.CleanupMinutes,
                        });
                    }
                }
            }

            // The order captures the product's requirement-configuration version at creation time so
            // later product edits never retroactively change an already-placed order's plan (0 = no
            // product in this order carried an active requirements version; Phase 0 has at most one
            // schedulable item so a single order-level version is sufficient — see decisions list).
            order.ResourceRequirementsVersion = request.Items
                .Select(line => products.TryGetValue(line.ProductId, out var p) ? p.ResourceRequirementsVersion : 0)
                .DefaultIfEmpty(0)
                .Max();

            order.TotalAmount = anyOnRequest ? null : CatalogMoney.Round(total ?? 0m);
            order.RecordHistory(
                CatalogOrderStatus.Requested,
                CatalogActorType.Customer,
                customer.Id,
                reason: null);

            dbContext.CatalogOrders.Add(order);

            var queued = await domainEvents.DispatchAsync(
                new CatalogOrderEvent(order, CatalogEventTypes.OrderCreated),
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

            return await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: false, cancellationToken);
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

        return await CatalogOrderPlanningMapper.EnrichAsync(dbContext, orders, includeAllocationDetail: false, cancellationToken);
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

        return order is null
            ? null
            : await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: false, cancellationToken);
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

        var policy = await CatalogPolicyService.ReadAsync(dbContext, EnsureTenant(), cancellationToken);
        if (!policy.CustomerCanCancelOrders)
            throw new InvalidCatalogOrderTransitionException("O tenant não permite cancelamento de pedidos pelo cliente.");
        order.Cancel(CatalogActorType.Customer, reason: null, customerId, policy.OrderCancelableUpToStatus);

        await domainEvents.EnsureReadyAsync(cancellationToken);

        await using var transaction = dbContext.Database.IsRelational()
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            // Unstarted allocations are released transactionally on cancel regardless of actor
            // (order-equipment-link-codex §3/§7); an already-started allocation stays occupied.
            await CatalogAllocationExecutionService.ReleaseUnstartedAllocationsHook(occupancy, dbContext, order, cancellationToken);

            // Customer cancelled their own order, so there is no customer-facing notice; staff get an
            // in-app heads-up via the dedicated event.
            var queued = await domainEvents.DispatchAsync(
                new CatalogOrderEvent(order, CatalogEventTypes.OrderCancelledByCustomer),
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

            return await CatalogOrderPlanningMapper.EnrichOneAsync(dbContext, order, includeAllocationDetail: false, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw new InvalidCatalogOrderTransitionException("The order was modified by another user.");
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

    public async Task<ProductRequestResponse> CancelProductRequestAsync(
        Guid customerId, Guid requestId, CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        var request = await dbContext.ProductRequests.Include(r => r.Files)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.CustomerId == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Solicitação não encontrada.");
        var policy = await CatalogPolicyService.ReadAsync(dbContext, tenantId, cancellationToken);
        if (!policy.CustomerCanCancelRequests)
            throw new InvalidCatalogOrderTransitionException("O tenant não permite cancelamento de solicitações pelo cliente.");
        if (request.Status != ProductRequestStatus.Submitted
            || policy.RequestCancelableUpToStatus != ProductRequestStatus.Submitted)
            throw new InvalidCatalogOrderTransitionException(
                $"A solicitação no status {request.Status} não permite cancelamento pelo cliente.");
        request.Status = ProductRequestStatus.Cancelled;
        request.Touch();
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidCatalogOrderTransitionException("A solicitação foi alterada por outro usuário.");
        }
        return CatalogOrderService.MapProductRequest(request);
    }

    public async Task<ProductRequestResponse> CreateProductRequestAsync(
        Guid customerId,
        CreateProductRequestDto request,
        IReadOnlyList<PortalUpload> files,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenant();
        ValidateCustomerNote(request.Note);
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

    public async Task<PagedResult<PortalProductRequestResponse>> ListOwnProductRequestsAsync(
        Guid customerId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        ValidatePage(page, pageSize);

        var filtered = dbContext.ProductRequests
            .AsNoTracking()
            .Where(request => request.CustomerId == customerId);

        var total = await filtered.CountAsync(cancellationToken);
        var skip = (page - 1L) * pageSize;
        if (skip >= total)
        {
            return new PagedResult<PortalProductRequestResponse>([], page, pageSize, total);
        }

        var requests = await filtered
            .Include(request => request.Files)
            .OrderByDescending(request => request.CreatedAt)
            .ThenByDescending(request => request.Id)
            .Skip((int)skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = new List<PortalProductRequestResponse>(requests.Count);
        foreach (var request in requests)
        {
            items.Add(await MapPortalProductRequestAsync(request, cancellationToken));
        }

        return new PagedResult<PortalProductRequestResponse>(items, page, pageSize, total);
    }

    public async Task<PortalProductRequestResponse?> GetOwnProductRequestAsync(
        Guid customerId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        EnsureTenant();
        var request = await dbContext.ProductRequests
            .AsNoTracking()
            .Include(row => row.Files)
            .FirstOrDefaultAsync(
                row => row.Id == requestId && row.CustomerId == customerId,
                cancellationToken);

        return request is null
            ? null
            : await MapPortalProductRequestAsync(request, cancellationToken);
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

    private async Task<PortalProductRequestResponse> MapPortalProductRequestAsync(
        ProductRequest request,
        CancellationToken cancellationToken)
    {
        var ttl = TimeSpan.FromSeconds(Math.Max(60, storageOptions.Value.SignedUrlTtlSeconds));
        var files = new List<PortalProductRequestFileDto>(request.Files.Count);
        foreach (var file in request.Files)
        {
            var url = await storageProvider.CreateSignedUrlAsync(
                storageOptions.Value.PrivateBucket,
                file.StorageKey,
                ttl,
                cancellationToken);
            files.Add(new PortalProductRequestFileDto(file.Id, file.FileName, url));
        }

        return new PortalProductRequestResponse(
            request.Id,
            request.Status,
            request.Description,
            request.Quantity,
            request.Note,
            request.CreatedAt,
            files);
    }

    private static void ValidateCustomerNote(string? note)
    {
        if (note is { Length: > 1000 })
        {
            throw new ArgumentException("A observação deve ter no máximo 1000 caracteres.");
        }
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1)
        {
            throw new ArgumentException("Page must be at least 1.");
        }

        if (pageSize < 1 || pageSize > DirectoryPage.MaxPageSize)
        {
            throw new ArgumentException($"Page size must be between 1 and {DirectoryPage.MaxPageSize}.");
        }
    }

    private async Task<IReadOnlyList<PortalProductResponse>> MapProductsAsync(IReadOnlyList<CatalogProduct> products, CancellationToken ct)
    {
        var assignments = await CatalogTaxonomy.LoadAsync(dbContext, EnsureTenant(), products.Select(x => x.Id), ct);
        var result = new List<PortalProductResponse>(products.Count);
        foreach (var p in products)
        {
            result.Add(await MapPortalProductAsync(p, ct) with
            {
                Taxonomy = assignments[p.Id],
                // Only products that passed ApplyVisibility reach here, so Restricted == granted to this customer.
                IsExclusive = p.Visibility == CatalogProductVisibility.Restricted,
            });
        }
        return result;
    }

    private async Task<PortalProductResponse> MapPortalProductAsync(CatalogProduct product, CancellationToken ct)
    {
        // Restricted products keep their files in the private bucket (no permanent public URL); they get
        // a short-lived signed URL, issued only after ApplyVisibility let the product through.
        var ttl = TimeSpan.FromSeconds(Math.Max(60, storageOptions.Value.SignedUrlTtlSeconds));
        var files = new List<PortalProductFileDto>();
        foreach (var f in product.Files.Where(f => f.Visibility == CatalogFileVisibility.CustomerVisible && f.Status == CatalogFileStatus.Ready))
        {
            string url;
            if (CatalogFileBuckets.IsPublic(product.Visibility, f.Visibility))
            {
                url = storageProvider.GetPublicUrl(storageOptions.Value.PublicBucket, f.StorageKey);
            }
            else
            {
                try
                {
                    url = await storageProvider.CreateSignedUrlAsync(storageOptions.Value.PrivateBucket, f.StorageKey, ttl, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missing/unsignable object must not break the whole catalog page; skip the file.
                    continue;
                }
            }
            files.Add(new PortalProductFileDto(f.Id, f.FileName, f.MimeType, f.SizeBytes, url));
        }

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
        tenantProvider.TenantId is Guid id && id != Guid.Empty
            ? id : throw new UnauthorizedAccessException("Tenant context is required.");
}
