using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authentication;
using Platform.Api.Common;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Rentals.Controllers;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Idempotency;

/// <summary>
/// The optional <c>Idempotency-Key</c> header on the customer order create and the customer
/// reservation create: the real portal service creates the orders, a counting service stands in for
/// the reservation create.
/// </summary>
public sealed class IdempotentCreateControllerTests
{
    private const string Key = "checkout-attempt-1";

    [Fact]
    public async Task Order_with_the_same_key_twice_creates_one_order_and_returns_the_same_body()
    {
        await using var h = await OrderHarness.CreateAsync();
        var request = h.Request(quantity: 2);

        var first = await h.Controller().CreateOrder(request, Key, CancellationToken.None);
        var second = await h.Controller().CreateOrder(request, Key, CancellationToken.None);

        var firstOk = Assert.IsType<OkObjectResult>(first.Result);
        var secondOk = Assert.IsType<OkObjectResult>(second.Result);
        Assert.Equal(200, firstOk.StatusCode);
        Assert.Equal(200, secondOk.StatusCode);
        Assert.Equal(Json(firstOk.Value), Json(secondOk.Value));
        Assert.Equal(1, await h.Db.CatalogOrders.CountAsync());
        var stored = await h.Db.IdempotencyKeys.SingleAsync();
        Assert.Equal(((CatalogOrderResponse)firstOk.Value!).Id, stored.ResourceId);
        Assert.Equal(200, stored.ResponseStatus);
        Assert.Equal(h.CustomerId, stored.PrincipalId);
    }

    [Fact]
    public async Task Order_with_the_same_key_and_a_different_body_answers_409_key_reused()
    {
        await using var h = await OrderHarness.CreateAsync();
        await h.Controller().CreateOrder(h.Request(quantity: 1), Key, CancellationToken.None);

        var reused = await h.Controller().CreateOrder(h.Request(quantity: 5), Key, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(reused.Result);
        Assert.Equal(IdempotencyErrorCodes.KeyReused, CodeOf(conflict.Value));
        Assert.Equal("idempotency.key_reused", IdempotencyErrorCodes.KeyReused);
        Assert.Equal(1, await h.Db.CatalogOrders.CountAsync());
    }

    [Fact]
    public async Task Order_without_the_header_behaves_as_before_and_stores_nothing()
    {
        await using var h = await OrderHarness.CreateAsync();
        var request = h.Request(quantity: 1);

        var first = await h.Controller().CreateOrder(request, null, CancellationToken.None);
        var second = await h.Controller().CreateOrder(request, null, CancellationToken.None);

        Assert.IsType<OkObjectResult>(first.Result);
        Assert.IsType<OkObjectResult>(second.Result);
        Assert.Equal(2, await h.Db.CatalogOrders.CountAsync());
        Assert.Equal(0, await h.Db.IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task Order_with_the_same_key_from_another_customer_creates_a_second_order()
    {
        await using var h = await OrderHarness.CreateAsync();
        var request = h.Request(quantity: 1);

        var first = await h.Controller().CreateOrder(request, Key, CancellationToken.None);
        var other = await h.Controller(h.OtherCustomerId).CreateOrder(request, Key, CancellationToken.None);

        var firstId = ((CatalogOrderResponse)Assert.IsType<OkObjectResult>(first.Result).Value!).Id;
        var otherId = ((CatalogOrderResponse)Assert.IsType<OkObjectResult>(other.Result).Value!).Id;
        Assert.NotEqual(firstId, otherId);
        Assert.Equal(2, await h.Db.CatalogOrders.CountAsync());
        Assert.Equal(2, await h.Db.IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task Order_that_fails_releases_the_key_so_the_retry_runs_again()
    {
        await using var h = await OrderHarness.CreateAsync();
        var unavailable = new CreatePortalOrderRequest
        {
            Items = [new CreatePortalOrderItemRequest { ProductId = Guid.NewGuid(), Quantity = 1 }],
        };

        var failed = await h.Controller().CreateOrder(unavailable, Key, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(failed.Result);
        Assert.Equal(0, await h.Db.IdempotencyKeys.CountAsync());

        // The same key with a valid body now creates the order: the failed attempt held nothing.
        var retry = await h.Controller().CreateOrder(h.Request(quantity: 1), Key, CancellationToken.None);
        Assert.IsType<OkObjectResult>(retry.Result);
        Assert.Equal(1, await h.Db.CatalogOrders.CountAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Order_with_a_blank_key_answers_400_and_creates_nothing(string key)
    {
        await using var h = await OrderHarness.CreateAsync();

        var result = await h.Controller().CreateOrder(h.Request(quantity: 1), key, CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(IdempotencyErrorCodes.KeyInvalid, CodeOf(bad.Value));
        Assert.Equal(0, await h.Db.CatalogOrders.CountAsync());
    }

    [Fact]
    public async Task Order_with_a_key_longer_than_100_characters_answers_400()
    {
        await using var h = await OrderHarness.CreateAsync();

        var result = await h.Controller().CreateOrder(
            h.Request(quantity: 1), new string('k', 101), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(0, await h.Db.CatalogOrders.CountAsync());
    }

    [Fact]
    public async Task Reservation_with_the_same_key_twice_creates_once_and_replays_201_with_the_same_body()
    {
        var reservations = new CountingReservationService();
        var h = ReservationHarness.Create(reservations);
        var request = ReservationRequest(startHour: 10);

        var first = await h.Controller(h.CustomerId).Create(request, Key, CancellationToken.None);
        var second = await h.Controller(h.CustomerId).Create(request, Key, CancellationToken.None);

        var firstCreated = Assert.IsType<CreatedResult>(first.Result);
        var secondCreated = Assert.IsType<CreatedResult>(second.Result);
        Assert.Equal(201, secondCreated.StatusCode);
        Assert.Equal(firstCreated.Location, secondCreated.Location);
        Assert.Equal(Json(firstCreated.Value), Json(secondCreated.Value));
        Assert.Equal(1, reservations.CreateCalls);
        var stored = await h.Db().IdempotencyKeys.SingleAsync();
        Assert.Equal(201, stored.ResponseStatus);
        Assert.Equal(((ReservationResponseDto)firstCreated.Value!).Id, stored.ResourceId);
    }

    [Fact]
    public async Task Reservation_with_the_same_key_and_a_different_body_answers_409_key_reused()
    {
        var reservations = new CountingReservationService();
        var h = ReservationHarness.Create(reservations);
        await h.Controller(h.CustomerId).Create(ReservationRequest(startHour: 10), Key, CancellationToken.None);

        var reused = await h.Controller(h.CustomerId).Create(ReservationRequest(startHour: 11), Key, CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(reused.Result);
        Assert.Equal(IdempotencyErrorCodes.KeyReused, CodeOf(conflict.Value));
        Assert.Equal(1, reservations.CreateCalls);
    }

    [Fact]
    public async Task Reservation_without_the_header_creates_every_time()
    {
        var reservations = new CountingReservationService();
        var h = ReservationHarness.Create(reservations);
        var request = ReservationRequest(startHour: 10);

        await h.Controller(h.CustomerId).Create(request, null, CancellationToken.None);
        await h.Controller(h.CustomerId).Create(request, null, CancellationToken.None);

        Assert.Equal(2, reservations.CreateCalls);
        Assert.Equal(0, await h.Db().IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task Reservation_with_the_same_key_from_another_customer_creates_a_second_one()
    {
        var reservations = new CountingReservationService();
        var h = ReservationHarness.Create(reservations);
        var request = ReservationRequest(startHour: 10);

        await h.Controller(h.CustomerId).Create(request, Key, CancellationToken.None);
        await h.Controller(Guid.NewGuid()).Create(request, Key, CancellationToken.None);

        Assert.Equal(2, reservations.CreateCalls);
        Assert.Equal(2, await h.Db().IdempotencyKeys.CountAsync());
    }

    [Fact]
    public async Task Reservation_that_fails_releases_the_key()
    {
        var reservations = new CountingReservationService { FailNext = true };
        var h = ReservationHarness.Create(reservations);
        var request = ReservationRequest(startHour: 10);

        var failed = await h.Controller(h.CustomerId).Create(request, Key, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(failed.Result);
        Assert.Equal(0, await h.Db().IdempotencyKeys.CountAsync());

        var retry = await h.Controller(h.CustomerId).Create(request, Key, CancellationToken.None);
        Assert.IsType<CreatedResult>(retry.Result);
        Assert.Equal(2, reservations.CreateCalls);
    }

    [Fact]
    public async Task Reservation_get_mine_returns_only_the_own_booking()
    {
        await using var h = await LocationBookingHarness.CreateAsync();
        var start = LocationBookingHarness.BookingClock.GetUtcNow().AddDays(3);
        var mine = h.SeedOverlappingReservation(ReservationStatus.Confirmed, start, start.AddHours(1));
        h.SeedOverlappingReservation(ReservationStatus.Confirmed, start.AddHours(3), start.AddHours(4));
        await h.Db.SaveChangesAsync();
        var service = h.CreateReservationService();

        var found = await service.GetMineAsync(h.CustomerId, mine.Id, CancellationToken.None);
        var listed = (await service.ListMineAsync(h.CustomerId, CancellationToken.None)).Single(r => r.Id == mine.Id);

        Assert.NotNull(found);
        Assert.Equal(Json(listed), Json(found));
        Assert.Null(await service.GetMineAsync(Guid.NewGuid(), mine.Id, CancellationToken.None));
        Assert.Null(await service.GetMineAsync(h.CustomerId, Guid.NewGuid(), CancellationToken.None));
    }

    private static CreateReservationRequestDto ReservationRequest(int startHour) => new()
    {
        UnitId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Date = new DateOnly(2027, 1, 10),
        StartTime = new TimeOnly(startHour, 0),
        EndTime = new TimeOnly(startHour + 1, 0),
        Items = [new CreateReservationItemRequestDto { AssetId = Guid.Parse("22222222-2222-2222-2222-222222222222"), Quantity = 1 }],
    };

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static string? CodeOf(object? body) =>
        JsonDocument.Parse(JsonSerializer.Serialize(body)).RootElement.GetProperty("code").GetString();

    internal static T WithCustomer<T>(T controller, Guid customerId)
        where T : ControllerBase
    {
        var identity = new ClaimsIdentity(
            [new Claim(CustomerClaimTypes.CustomerId, customerId.ToString())],
            "test");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return controller;
    }

    private sealed class OrderHarness : IAsyncDisposable
    {
        private readonly CatalogHarness harness;

        private OrderHarness(CatalogHarness harness, Guid productId, Guid customerId, Guid otherCustomerId)
        {
            this.harness = harness;
            ProductId = productId;
            CustomerId = customerId;
            OtherCustomerId = otherCustomerId;
        }

        public AppDbContext Db => harness.Db;

        public Guid ProductId { get; }

        public Guid CustomerId { get; }

        public Guid OtherCustomerId { get; }

        public static async Task<OrderHarness> CreateAsync()
        {
            var harness = await CatalogHarness.CreateAsync();
            var product = await harness.Products.CreateAsync(
                new CreateCatalogProductRequest { Name = "Item", Price = 10m, Code = "A1" },
                CancellationToken.None);
            var customer = harness.AddCustomer();
            var other = harness.AddCustomer("Bia");
            await harness.Db.SaveChangesAsync();
            return new OrderHarness(harness, product.Id, customer.Id, other.Id);
        }

        public CreatePortalOrderRequest Request(int quantity) => new()
        {
            Items = [new CreatePortalOrderItemRequest { ProductId = ProductId, Quantity = quantity }],
        };

        public CatalogPortalController Controller(Guid? customerId = null)
        {
            // A new service per call, like a new request, over the same store.
            var idempotency = new IdempotencyService(harness.Db, harness.TenantProvider);
            return WithCustomer(
                new CatalogPortalController(harness.Portal, idempotency),
                customerId ?? CustomerId);
        }

        public async ValueTask DisposeAsync() => await harness.DisposeAsync();
    }

    private sealed class ReservationHarness
    {
        private readonly string databaseName = $"idem-res-{Guid.NewGuid():N}";
        private readonly CountingReservationService reservations;
        private readonly FakeTenantProvider tenants = new() { TenantId = Guid.NewGuid() };

        private ReservationHarness(CountingReservationService reservations) => this.reservations = reservations;

        public Guid CustomerId { get; } = Guid.NewGuid();

        public static ReservationHarness Create(CountingReservationService reservations) => new(reservations);

        public AppDbContext Db() =>
            InMemoryAppDb.Create(tenants, databaseName);

        public ReservationsController Controller(Guid customerId) =>
            WithCustomer(
                new ReservationsController(
                    reservations,
                    rentalAssetService: null!,
                    publicTenantBinder: null!,
                    new IdempotencyService(Db(), tenants)),
                customerId);
    }
}
