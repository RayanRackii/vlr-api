using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogCancelledByCustomerTests
{
    [Fact]
    public async Task Customer_cancel_emits_cancelled_by_customer_event_to_staff_only()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var staff = harness.AddManageUser();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);
        var customer = harness.AddCustomer();
        await harness.Db.SaveChangesAsync();

        var order = await harness.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
            },
            CancellationToken.None);

        await harness.Portal.CancelOrderAsync(customer.Id, order.Id, CancellationToken.None);

        var cancelNotification = await harness.Db.Notifications
            .Include(n => n.Deliveries)
            .SingleAsync(n => n.EventType == CatalogEventTypes.OrderCancelledByCustomer
                && n.AggregateId == order.Id);

        var delivery = Assert.Single(cancelNotification.Deliveries);
        Assert.Equal(NotificationChannel.InApp, delivery.Channel);
        Assert.Equal(NotificationRecipientKind.B2BUser, delivery.RecipientKind);
        Assert.Equal(staff.Id, delivery.RecipientId);
        Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
        // Customer cancelled, so there is no customer-facing delivery for this event.
        Assert.DoesNotContain(cancelNotification.Deliveries, d => d.RecipientKind == NotificationRecipientKind.Customer);
    }
}
