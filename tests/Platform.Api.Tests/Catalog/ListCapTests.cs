using Microsoft.AspNetCore.Http;
using Platform.Api.Common;
using Platform.Core.Domain.Entities;

namespace Platform.Api.Tests.Catalog;

public sealed class ListCapTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Product_request_list_is_capped_and_reports_the_real_total()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer("Maria");
        await h.Db.SaveChangesAsync(Ct);
        for (var i = 0; i < ListCap.MaxRows + 1; i++)
        {
            h.Db.ProductRequests.Add(new ProductRequest
            {
                TenantId = h.Tenant.Id,
                CustomerId = customer.Id,
                Description = $"Item {i}",
                Quantity = 1,
            });
        }

        await h.Db.SaveChangesAsync(Ct);
        h.Http.HttpContext = new DefaultHttpContext();

        var list = await h.Orders.ListProductRequestsAsync(Ct);

        Assert.Equal(ListCap.MaxRows, list.Count);
        Assert.Equal(
            (ListCap.MaxRows + 1).ToString(),
            h.Http.HttpContext.Response.Headers[ListCap.TotalCountHeader].ToString());
    }

    [Fact]
    public async Task Under_the_cap_the_total_equals_the_returned_rows()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var customer = h.AddCustomer("Maria");
        await h.Db.SaveChangesAsync(Ct);
        h.Db.ProductRequests.Add(new ProductRequest
        {
            TenantId = h.Tenant.Id,
            CustomerId = customer.Id,
            Description = "Item",
            Quantity = 1,
        });
        await h.Db.SaveChangesAsync(Ct);
        h.Http.HttpContext = new DefaultHttpContext();

        var list = await h.Orders.ListProductRequestsAsync(Ct);

        Assert.Single(list);
        Assert.Equal("1", h.Http.HttpContext.Response.Headers[ListCap.TotalCountHeader].ToString());
    }

    [Fact]
    public async Task Publishing_without_an_http_context_is_a_no_op()
    {
        var counted = false;

        await ListCap.PublishTotalAsync(
            null,
            ListCap.MaxRows,
            () =>
            {
                counted = true;
                return Task.FromResult(1);
            });

        Assert.False(counted);
    }
}
