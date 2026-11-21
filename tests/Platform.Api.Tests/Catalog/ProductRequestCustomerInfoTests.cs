using Platform.Api.Modules.Catalog.Dtos;

namespace Platform.Api.Tests.Catalog;

public sealed class ProductRequestCustomerInfoTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Staff_list_and_detail_include_customer_name_and_email()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer("Maria Souza");
        await h.Db.SaveChangesAsync(Ct);
        var created = await h.Portal.CreateProductRequestAsync(customer.Id,
            new CreateProductRequestDto { Description = "Item", Quantity = 2 }, [], Ct);

        var list = await h.Orders.ListProductRequestsAsync(Ct);
        var listed = Assert.Single(list);
        Assert.Equal("Maria Souza", listed.CustomerName);
        Assert.Equal(customer.Email, listed.CustomerEmail);

        var detail = await h.Orders.GetProductRequestAsync(created.Id, Ct);
        Assert.NotNull(detail);
        Assert.Equal("Maria Souza", detail!.CustomerName);
        Assert.Equal(customer.Email, detail.CustomerEmail);
    }
}
