using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogWhatsAppNotificationTests
{
    [Theory]
    [InlineData(CatalogEventTypes.OrderCreated, "Solicitado")]
    [InlineData(CatalogEventTypes.OrderApproved, "Aprovado")]
    [InlineData(CatalogEventTypes.OrderReady, "Pronto")]
    [InlineData(CatalogEventTypes.OrderRejected, "Recusado")]
    [InlineData(CatalogEventTypes.OrderCancelledBySupplier, "Cancelado")]
    public async Task Order_status_whatsapp_uses_approved_template(
        string eventType,
        string expectedStatus)
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await EnableWhatsAppAsync(harness, eventType);
        var deliveryId = await CreateAndTransitionAsync(harness, eventType);

        await harness.Processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None);

        Assert.Equal("catalog_order_status_update", harness.WhatsApp.LastTemplateName);
        Assert.Equal("pt_BR", harness.WhatsApp.LastLanguage);
        Assert.Equal(4, harness.WhatsApp.LastParameters.Count);
        Assert.Equal("Catalog Club", harness.WhatsApp.LastParameters[0]);
        Assert.Equal("Ana", harness.WhatsApp.LastParameters[1]);
        Assert.StartsWith("#", harness.WhatsApp.LastParameters[2], StringComparison.Ordinal);
        Assert.Equal(expectedStatus, harness.WhatsApp.LastParameters[3]);
        Assert.Equal(0, harness.WhatsApp.FreeTextCount);

        var delivery = await harness.Db.NotificationDeliveries.FindAsync(deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Sent, delivery!.Status);
        Assert.Equal("wamid.test", delivery.ProviderMessageId);
    }

    [Fact]
    public async Task WhatsApp_disabled_does_not_queue_external_delivery()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);
        var customer = harness.AddCustomer();
        await harness.Db.SaveChangesAsync();
        await harness.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
            },
            CancellationToken.None);

        Assert.DoesNotContain(
            harness.Db.NotificationDeliveries,
            d => d.Channel == NotificationChannel.WhatsApp);
        Assert.Equal(0, harness.WhatsApp.SendCount);
    }

    [Fact]
    public async Task Missing_template_name_is_permanent_and_does_not_send_free_text()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await EnableWhatsAppAsync(harness, CatalogEventTypes.OrderCreated);
        var deliveryId = await CreateAndTransitionAsync(harness, CatalogEventTypes.OrderCreated);

        var template = await harness.Db.NotificationTemplates.FirstAsync(t =>
            t.EventType == CatalogEventTypes.OrderCreated && t.Channel == NotificationChannel.WhatsApp);
        template.WhatsAppTemplateName = null;
        await harness.Db.SaveChangesAsync();

        await harness.Processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None);

        var delivery = await harness.Db.NotificationDeliveries.FindAsync(deliveryId);
        Assert.Equal(NotificationDeliveryStatus.Failed, delivery!.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(0, harness.WhatsApp.SendCount);
        Assert.Equal(0, harness.WhatsApp.FreeTextCount);
    }

    [Fact]
    public async Task Idempotent_process_does_not_double_send_whatsapp()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await EnableWhatsAppAsync(harness, CatalogEventTypes.OrderCreated);
        var deliveryId = await CreateAndTransitionAsync(harness, CatalogEventTypes.OrderCreated);

        await harness.Processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None);
        await harness.Processor.ProcessDeliveryAsync(deliveryId, CancellationToken.None);

        Assert.Equal(1, harness.WhatsApp.SendCount);
    }

    [Fact]
    public async Task Reconcile_rewrites_legacy_catalog_template_names()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        await harness.Publisher.EnsureReadyAsync(CancellationToken.None);

        var template = await harness.Db.NotificationTemplates.FirstAsync(t =>
            t.EventType == CatalogEventTypes.OrderCreated && t.Channel == NotificationChannel.WhatsApp);
        template.WhatsAppTemplateName = "catalog_order_created";
        await harness.Db.SaveChangesAsync();

        await harness.Publisher.EnsureReadyAsync(CancellationToken.None);

        var reloaded = await harness.Db.NotificationTemplates.FirstAsync(t =>
            t.EventType == CatalogEventTypes.OrderCreated && t.Channel == NotificationChannel.WhatsApp);
        Assert.Equal("catalog_order_status_update", reloaded.WhatsAppTemplateName);
    }

    private static async Task EnableWhatsAppAsync(CatalogHarness harness, string eventType)
    {
        await harness.Notifications.UpsertChannelConfigAsync(
            new UpsertCatalogChannelConfigRequest
            {
                EventType = eventType,
                Channel = NotificationChannel.WhatsApp,
                IsActive = true,
            },
            CancellationToken.None);
    }

    private static async Task<Guid> CreateAndTransitionAsync(CatalogHarness harness, string eventType)
    {
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

        if (eventType == CatalogEventTypes.OrderApproved)
        {
            await harness.Orders.ApproveAsync(order.Id, CancellationToken.None);
        }
        else if (eventType == CatalogEventTypes.OrderReady)
        {
            await harness.Orders.ApproveAsync(order.Id, CancellationToken.None);
            await harness.Orders.StartPreparingAsync(order.Id, CancellationToken.None);
            await harness.Orders.MarkReadyAsync(order.Id, CancellationToken.None);
        }
        else if (eventType == CatalogEventTypes.OrderRejected)
        {
            await harness.Orders.RejectAsync(order.Id, "sem estoque", CancellationToken.None);
        }
        else if (eventType == CatalogEventTypes.OrderCancelledBySupplier)
        {
            await harness.Orders.ApproveAsync(order.Id, CancellationToken.None);
            await harness.Orders.CancelAsync(order.Id, "fornecedor", CancellationToken.None);
        }

        var delivery = harness.Db.NotificationDeliveries
            .Include(d => d.Notification)
            .Single(d =>
            d.Channel == NotificationChannel.WhatsApp
            && d.Notification.EventType == eventType);
        return delivery.Id;
    }
}
