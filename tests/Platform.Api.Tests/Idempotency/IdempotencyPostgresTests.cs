using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Common;
using Platform.Api.Jobs;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Idempotency;

/// <summary>
/// Concurrent repeats of the same <c>Idempotency-Key</c> against a real PostgreSQL: the unique
/// index on (tenant, scope, customer, key) lets exactly one request create the resource.
/// </summary>
public sealed class IdempotencyPostgresTests : IClassFixture<PostgresContainerFixture>
{
    private const int Parallelism = 6;

    private readonly PostgresContainerFixture postgres;

    public IdempotencyPostgresTests(PostgresContainerFixture postgres) => this.postgres = postgres;

    [DockerFact]
    public async Task Parallel_orders_with_the_same_key_create_exactly_one_order()
    {
        var factory = RequireFactory();
        var tenants = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenants);
        var request = new CreatePortalOrderRequest
        {
            Items = [new CreatePortalOrderItemRequest { ProductId = seed.ProductId, Quantity = 1 }],
        };

        var results = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(async _ =>
        {
            await using var db = factory.Create(tenants);
            var controller = IdempotentCreateControllerTests.WithCustomer(
                new CatalogPortalController(
                    CreatePortal(db, tenants),
                    new IdempotencyService(db, tenants, TimeProvider.System, FastOptions)),
                seed.CustomerId);
            return await controller.CreateOrder(request, "parallel-key", CancellationToken.None);
        }));

        var bodies = results
            .Select(r => Assert.IsType<OkObjectResult>(r.Result).Value)
            .Cast<CatalogOrderResponse>()
            .ToList();
        Assert.Single(bodies.Select(b => b.Id).Distinct());

        await using var verify = factory.Create(tenants);
        Assert.Equal(1, await verify.CatalogOrders.CountAsync());
        var key = await verify.IdempotencyKeys.SingleAsync();
        Assert.Equal(bodies[0].Id, key.ResourceId);
        Assert.Equal(200, key.ResponseStatus);
        Assert.Equal(seed.CustomerId, key.PrincipalId);
    }

    [DockerFact]
    public async Task Parallel_reservations_with_the_same_key_run_the_create_once()
    {
        var factory = RequireFactory();
        var tenants = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenants);
        // The create is slow, so every other request arrives while the first one is still running.
        var reservations = new CountingReservationService { Delay = TimeSpan.FromMilliseconds(300) };
        var request = new CreateReservationRequestDto
        {
            UnitId = Guid.NewGuid(),
            Date = new DateOnly(2027, 1, 10),
            StartTime = new TimeOnly(10, 0),
            EndTime = new TimeOnly(11, 0),
            Items = [new CreateReservationItemRequestDto { AssetId = Guid.NewGuid(), Quantity = 1 }],
        };

        var results = await Task.WhenAll(Enumerable.Range(0, Parallelism).Select(async _ =>
        {
            await using var db = factory.Create(tenants);
            var controller = IdempotentCreateControllerTests.WithCustomer(
                new ReservationsController(
                    reservations,
                    rentalAssetService: null!,
                    publicTenantBinder: null!,
                    new IdempotencyService(db, tenants, TimeProvider.System, FastOptions)),
                seed.CustomerId);
            return await controller.Create(request, "parallel-key", CancellationToken.None);
        }));

        Assert.Equal(1, reservations.CreateCalls);
        var created = results.Select(r => Assert.IsType<CreatedResult>(r.Result)).ToList();
        Assert.Single(created.Select(c => ((ReservationResponseDto)c.Value!).Id).Distinct());
        Assert.Single(created.Select(c => c.Location).Distinct());

        await using var verify = factory.Create(tenants);
        var key = await verify.IdempotencyKeys.SingleAsync();
        Assert.Equal(201, key.ResponseStatus);
    }

    [DockerFact]
    public async Task The_unique_index_rejects_a_second_claim_of_the_same_key()
    {
        var factory = RequireFactory();
        var tenants = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenants);
        await using var db = factory.Create(tenants);
        db.IdempotencyKeys.Add(new IdempotencyKey(seed.TenantId, "s", seed.CustomerId, "k", "h", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        // Same tenant, scope, customer and key, but a different row id.
        await using var other = factory.Create(tenants);
        other.IdempotencyKeys.Add(new IdempotencyKey(seed.TenantId, "s", seed.CustomerId, "k", "h", DateTimeOffset.UtcNow));
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)error.InnerException!).SqlState);

        // Another customer or scope may use the same key.
        await using var distinct = factory.Create(tenants);
        distinct.IdempotencyKeys.Add(new IdempotencyKey(seed.TenantId, "s", Guid.NewGuid(), "k", "h", DateTimeOffset.UtcNow));
        distinct.IdempotencyKeys.Add(new IdempotencyKey(seed.TenantId, "t", seed.CustomerId, "k", "h", DateTimeOffset.UtcNow));
        await distinct.SaveChangesAsync();
    }

    [DockerFact]
    public async Task Retention_job_deletes_expired_keys_on_postgres()
    {
        var factory = RequireFactory();
        var tenants = new FakeTenantProvider();
        var seed = await SeedAsync(factory, tenants);
        var now = DateTimeOffset.UtcNow;
        await using (var db = factory.Create(tenants))
        {
            db.IdempotencyKeys.AddRange(
                new IdempotencyKey(seed.TenantId, "s", seed.CustomerId, "expired", "h", now.AddHours(-25)),
                new IdempotencyKey(seed.TenantId, "s", seed.CustomerId, "live", "h", now.AddHours(-1)));
            await db.SaveChangesAsync();
        }

        await using (var jobDb = factory.Create(new FakeTenantProvider()))
        {
            await new DataRetentionJob(jobDb, NullLogger<DataRetentionJob>.Instance)
                .ExecuteAsync(CancellationToken.None);
        }

        await using var verify = factory.Create(tenants);
        var left = await verify.IdempotencyKeys.Select(k => k.Key).ToListAsync();
        Assert.Equal(["live"], left);
    }

    private static readonly IdempotencyOptions FastOptions = new()
    {
        PendingWait = TimeSpan.FromSeconds(20),
        PollInterval = TimeSpan.FromMilliseconds(20),
    };

    private PostgresAppDbFactory RequireFactory()
    {
        Assert.NotNull(postgres.Factory);
        return postgres.Factory;
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

    private static async Task<Seed> SeedAsync(PostgresAppDbFactory factory, FakeTenantProvider tenantProvider)
    {
        await using var db = factory.Create(tenantProvider);
        var tenant = new Tenant(
            "Idem Club",
            Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(),
            subdomain: $"idm-{Guid.NewGuid():N}"[..12]);
        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        var customer = new Customer { TenantId = tenant.Id, Name = "Ana", Email = "ana@club.test" };
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
        await db.SaveChangesAsync();
        return new Seed(tenant.Id, customer.Id, product.Id);
    }

    private sealed record Seed(Guid TenantId, Guid CustomerId, Guid ProductId);
}
