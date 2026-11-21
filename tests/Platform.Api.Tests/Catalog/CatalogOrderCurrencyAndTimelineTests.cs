using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogOrderCurrencyAndTimelineTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Order_currency_follows_its_items()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var usd = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Import", Price = 10m, Currency = "usd" }, Ct);
        var customer = h.AddCustomer();
        await h.Db.SaveChangesAsync(Ct);

        var order = await h.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = usd.Id, Quantity = 2 }],
            },
            Ct);

        Assert.Equal("USD", order.Currency);
        Assert.Equal(20m, order.TotalAmount);
    }

    [Fact]
    public async Task Mixed_currency_cart_is_refused_with_the_mismatch_error_and_creates_nothing()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var brl = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Local", Price = 10m }, Ct);
        var usd = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Import", Price = 10m, Currency = "USD" }, Ct);
        var customer = h.AddCustomer();
        await h.Db.SaveChangesAsync(Ct);

        var ex = await Assert.ThrowsAsync<CurrencyMismatchException>(() => h.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items =
                [
                    new CreatePortalOrderItemRequest { ProductId = brl.Id, Quantity = 1 },
                    new CreatePortalOrderItemRequest { ProductId = usd.Id, Quantity = 1 },
                ],
            },
            Ct));

        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Equal("catalog.currency_mismatch", CurrencyMismatchException.Code);
        Assert.Empty(await h.Db.CatalogOrders.ToListAsync(Ct));
    }

    [Fact]
    public async Task Timeline_entries_carry_a_message_key_and_params_next_to_the_reason()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var product = await h.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m }, Ct);
        var customer = h.AddCustomer();
        await h.Db.SaveChangesAsync(Ct);
        var created = await h.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
            },
            Ct);

        var order = await h.Orders.GetByIdAsync(created.Id, Ct);

        Assert.NotNull(order);
        var entry = Assert.Single(order!.History);
        Assert.Equal("catalog.order.history.requested", entry.MessageKey);
        Assert.Null(entry.MessageParams);
        Assert.Null(entry.Reason);
    }

    [Theory]
    [InlineData("Requested", null, "catalog.order.history.requested")]
    [InlineData("Rejected", "Sem estoque", "catalog.order.history.rejected")]
    [InlineData("Completed", "usage.workFinished", "catalog.order.history.usageWorkFinished")]
    [InlineData("Completed", "usage.released", "catalog.order.history.usageReleased")]
    public void Message_key_is_derived_from_status_and_reason(string status, string? reason, string expectedKey)
    {
        var parsed = Enum.Parse<Platform.Core.Domain.Enums.CatalogOrderStatus>(status);

        Assert.Equal(expectedKey, CatalogOrderHistoryMessages.KeyFor(parsed, reason));
    }

    [Fact]
    public void Free_text_reason_is_passed_as_a_param_and_system_codes_are_not()
    {
        Assert.Equal("Sem estoque", CatalogOrderHistoryMessages.ParamsFor("Sem estoque")!["reason"]);
        Assert.Null(CatalogOrderHistoryMessages.ParamsFor("usage.released"));
        Assert.Null(CatalogOrderHistoryMessages.ParamsFor("  "));
        Assert.Null(CatalogOrderHistoryMessages.ParamsFor(null));
    }
}
