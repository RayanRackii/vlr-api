using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public CatalogConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Parallel_orders_get_unique_numbers()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var portal1 = CreatePortal(db1, tenantProvider);
        var portal2 = CreatePortal(db2, tenantProvider);

        var request = new CreatePortalOrderRequest
        {
            Items = [new CreatePortalOrderItemRequest { ProductId = seed.ProductId, Quantity = 1 }],
        };

        var captured = await Task.WhenAll(
            CaptureAsync(() => portal1.CreateOrderAsync(seed.CustomerId, request, CancellationToken.None)),
            CaptureAsync(() => portal2.CreateOrderAsync(seed.CustomerId, request, CancellationToken.None)));

        Assert.Equal(2, captured.Count(r => r.Error is null));
        Assert.Equal(2, captured.Select(r => r.Value!.OrderNumber).Distinct().Count());

        await using var verify = factory.Create(tenantProvider);
        var numbers = await verify.CatalogOrders.Select(o => o.OrderNumber).ToListAsync();
        Assert.Equal(2, numbers.Distinct().Count());
    }

    [DockerFact]
    public async Task Parallel_approve_allows_only_one_winner()
    {
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider, withOrder: true);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var orders1 = CreateOrders(db1, tenantProvider);
        var orders2 = CreateOrders(db2, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(() => orders1.ApproveAsync(seed.OrderId!.Value, CancellationToken.None)),
            CaptureAsync(() => orders2.ApproveAsync(seed.OrderId!.Value, CancellationToken.None)));

        Assert.Single(captured, r => r.Error is null);
        Assert.Contains(captured, r => r.Error is InvalidCatalogOrderTransitionException);
    }

    [DockerFact]
    public async Task Parallel_approve_and_cancel_ends_in_one_consistent_terminal_state()
    {
        // Cancel.EnsureStatus (CatalogOrder.cs) legitimately allows Requested, Approved, Preparing
        // and Ready — unlike two concurrent approves, approve-then-cancel is a VALID sequential
        // business transition (staff can cancel an order right after approving it), so both calls
        // may legitimately succeed if they do not truly overlap at the row-version level. The
        // invariant under race is not "exactly one wins": it is (a) no corrupted/unexpected error
        // type ever surfaces, and (b) the persisted order ends in exactly one well-formed terminal
        // status, never a torn/partial write.
        var factory = RequireFactory();
        var tenantProvider = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenantProvider, withOrder: true);

        await using var db1 = factory.Create(tenantProvider);
        await using var db2 = factory.Create(tenantProvider);
        var orders1 = CreateOrders(db1, tenantProvider);
        var orders2 = CreateOrders(db2, tenantProvider);

        var captured = await Task.WhenAll(
            CaptureAsync(() => orders1.ApproveAsync(seed.OrderId!.Value, CancellationToken.None)),
            CaptureAsync(() => orders2.CancelAsync(seed.OrderId!.Value, "preflight race", CancellationToken.None)));

        Assert.All(captured, r => Assert.True(
            r.Error is null or InvalidCatalogOrderTransitionException,
            $"Unexpected error type: {r.Error}"));

        await using var verify = factory.Create(tenantProvider);
        var order = await verify.CatalogOrders
            .Include(o => o.History)
            .SingleAsync(o => o.Id == seed.OrderId);
        Assert.True(order.Status is CatalogOrderStatus.Approved or CatalogOrderStatus.Cancelled);
        // Exactly one history row per status actually reached — no duplicate/torn transition.
        Assert.Equal(1, order.History.Count(h => h.Status == order.Status));
    }

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(_postgres.Factory);
        return _postgres.Factory;
    }

    private static CatalogPortalService CreatePortal(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var storageOptions = Options.Create(new StorageOptions());
        var storage = new DevStorageProvider(new FakeHostEnvironment(), storageOptions);
        var publisher = new CatalogNotificationPublisher(
            db,
            tenantProvider,
            new FakePermissionResolver(),
            NullLogger<CatalogNotificationPublisher>.Instance);
        return new CatalogPortalService(
            db,
            tenantProvider,
            storage,
            storageOptions,
            publisher.AsDomainEvents(),
            new RecordingOutboxScheduler(),
            new Platform.Api.Modules.Rentals.Services.ResourceOccupancyService());
    }

    private static CatalogOrderService CreateOrders(AppDbContext db, FakeTenantProvider tenantProvider)
    {
        var publisher = new CatalogNotificationPublisher(
            db,
            tenantProvider,
            new FakePermissionResolver(),
            NullLogger<CatalogNotificationPublisher>.Instance);
        var occupancy = new Platform.Api.Modules.Rentals.Services.ResourceOccupancyService();
        var scheduler = new RecordingOutboxScheduler();
        var allocationService = new CatalogOrderAllocationService(
            db, tenantProvider, occupancy, publisher.AsDomainEvents(), scheduler);
        return new CatalogOrderService(
            db,
            tenantProvider,
            new FakeHttpContextAccessor(),
            publisher.AsDomainEvents(),
            scheduler,
            allocationService,
            occupancy);
    }

    private static async Task<Seed> SeedAsync(
        PostgresAppDbFactory factory,
        FakeTenantProvider tenantProvider,
        bool withOrder = false)
    {
        await using var db = factory.Create(tenantProvider);
        var tenant = new Tenant("Lock Club", UniqueTaxId(), subdomain: $"clk-{Guid.NewGuid():N}"[..12]);
        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Ana",
            Email = "ana@club.test",
        };
        db.Customers.Add(customer);
        var product = new CatalogProduct
        {
            TenantId = tenant.Id,
            Name = "Item",
            Price = 10m,
            Currency = "BRL",
            IsActive = true,
        };
        db.CatalogProducts.Add(product);
        Guid? orderId = null;
        if (withOrder)
        {
            db.CatalogOrderNumberSequences.Add(new CatalogOrderNumberSequence
            {
                TenantId = tenant.Id,
                LastNumber = 1,
            });
            var order = new CatalogOrder
            {
                TenantId = tenant.Id,
                CustomerId = customer.Id,
                OrderNumber = 1,
                CustomerNameSnapshot = customer.Name,
                Currency = "BRL",
            };
            order.AddItem(new CatalogOrderItem
            {
                TenantId = tenant.Id,
                OrderId = order.Id,
                ProductId = product.Id,
                ProductNameSnapshot = product.Name,
                Quantity = 1,
                UnitPriceSnapshot = 10m,
                SubTotal = 10m,
                Currency = "BRL",
            });
            db.CatalogOrders.Add(order);
            orderId = order.Id;
        }

        await db.SaveChangesAsync();
        return new Seed(customer.Id, product.Id, orderId);
    }

    private static string UniqueTaxId() =>
        Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString();

    private static async Task<Capture<T>> CaptureAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return new Capture<T>(await action(), null);
        }
        catch (Exception ex)
        {
            return new Capture<T>(default, ex);
        }
    }

    private sealed record Seed(Guid CustomerId, Guid ProductId, Guid? OrderId);

    private sealed record Capture<T>(T? Value, Exception? Error);
}
