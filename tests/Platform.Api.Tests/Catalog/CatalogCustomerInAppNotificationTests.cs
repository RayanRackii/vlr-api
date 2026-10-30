using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Tests.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogCustomerInAppNotificationTests
{
    [Fact]
    public async Task Preparing_is_visible_in_customer_inbox_with_ptbr_title_and_order_route()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await harness.Publisher.EnsureReadyAsync();
        var customer = harness.AddCustomer();
        var order = new CatalogOrder
        {
            TenantId = harness.Tenant.Id,
            CustomerId = customer.Id,
            CustomerNameSnapshot = customer.Name,
            OrderNumber = 12,
        };
        await harness.Publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderPreparing);
        await harness.Db.SaveChangesAsync();
        var service = new CustomerNotificationsService(harness.Db);
        var inbox = await service.ListAsync(CustomerNotificationsServiceTests.Principal(harness.Tenant.Id, customer.Id),
            null, false, 20, "pt-BR", CancellationToken.None);
        var item = Assert.Single(inbox.Items);
        Assert.Equal("Pedido #12 em preparação.", item.Title);
        Assert.Equal("catalog", item.Module);
        Assert.Equal($"/t/{harness.Tenant.Subdomain}/pedidos/{order.Id}", item.TargetRoute);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Configured_email_is_preserved_and_inapp_is_never_duplicated(bool inAppEnabled)
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await harness.Publisher.EnsureReadyAsync();
        foreach (var config in harness.Db.TenantNotificationChannelConfigs.Where(c => c.EventType == CatalogEventTypes.OrderApproved))
        {
            config.IsActive = config.Channel == NotificationChannel.Email
                || (config.Channel == NotificationChannel.InApp && inAppEnabled);
        }

        await harness.Db.SaveChangesAsync();
        var customer = harness.AddCustomer();
        var order = new CatalogOrder
        {
            TenantId = harness.Tenant.Id,
            CustomerId = customer.Id,
            CustomerNameSnapshot = customer.Name,
            CustomerEmailSnapshot = customer.Email,
        };
        var queued = await harness.Publisher.PublishOrderEventAsync(order, CatalogEventTypes.OrderApproved);
        await harness.Db.SaveChangesAsync();
        var deliveries = await harness.Db.NotificationDeliveries.ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.Single(deliveries, d => d.Channel == NotificationChannel.InApp);
        var email = Assert.Single(deliveries, d => d.Channel == NotificationChannel.Email);
        Assert.Equal(email.Id, Assert.Single(queued));
        Assert.Equal(NotificationDeliveryStatus.Queued, email.Status);
    }

    [Theory]
    [InlineData(CatalogEventTypes.OrderCreated)]
    [InlineData(CatalogEventTypes.OrderApproved)]
    [InlineData(CatalogEventTypes.OrderPreparing)]
    [InlineData(CatalogEventTypes.OrderReady)]
    [InlineData(CatalogEventTypes.OrderRejected)]
    [InlineData(CatalogEventTypes.OrderCancelledBySupplier)]
    public async Task Customer_always_gets_one_delivered_inapp_even_with_all_channels_disabled(string eventType)
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await harness.Publisher.EnsureReadyAsync();
        foreach (var config in harness.Db.TenantNotificationChannelConfigs)
        {
            config.IsActive = false;
        }

        await harness.Db.SaveChangesAsync();
        var customer = harness.AddCustomer();
        var order = new CatalogOrder
        {
            TenantId = harness.Tenant.Id,
            CustomerId = customer.Id,
            CustomerNameSnapshot = customer.Name,
            OrderNumber = 12,
        };

        var queued = await harness.Publisher.PublishOrderEventAsync(order, eventType);
        await harness.Db.SaveChangesAsync();

        Assert.Empty(queued);
        var delivery = Assert.Single(await harness.Db.NotificationDeliveries.Include(d => d.Notification).ToListAsync());
        Assert.Equal(NotificationChannel.InApp, delivery.Channel);
        Assert.Equal(NotificationDeliveryStatus.Delivered, delivery.Status);
        Assert.Equal(NotificationRecipientKind.Customer, delivery.RecipientKind);
        Assert.Equal(customer.Id, delivery.RecipientId);
        Assert.Equal(harness.Tenant.Id, delivery.TenantId);
        Assert.Equal(eventType, delivery.Notification.EventType);
        Assert.Equal(PlatformModules.Catalog, delivery.Notification.Payload["module"]);
        Assert.Equal($"/t/{harness.Tenant.Subdomain}/pedidos/{order.Id}", delivery.Notification.Payload["targetRoute"]);
        var template = await harness.Db.NotificationTemplates.SingleAsync(t =>
            t.EventType == eventType && t.Channel == NotificationChannel.InApp && t.Language == "pt-BR");
        Assert.Contains("Pedido {{orderNumber}}", template.BodyTemplate);
    }
}
