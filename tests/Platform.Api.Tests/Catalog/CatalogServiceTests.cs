using Platform.Api.Modules.Catalog.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Domain.Exceptions;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogServiceTests
{
    [Fact]
    public async Task Products_are_tenant_scoped_and_deactivate_instead_of_delete()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var created = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Peça A", Price = 10.5m },
            CancellationToken.None);

        var otherProvider = new Fakes.FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var otherDb = Infrastructure.InMemoryAppDb.Create(otherProvider);
        otherDb.Tenants.Add(new Tenant("Other", "55555555000191"));
        await otherDb.SaveChangesAsync();
        harness.TenantProvider.TenantId = otherProvider.TenantId;

        Assert.Null(await harness.Products.GetByIdAsync(created.Id, CancellationToken.None));

        harness.TenantProvider.TenantId = harness.Tenant.Id;
        var deactivated = await harness.Products.DeactivateAsync(created.Id, CancellationToken.None);
        Assert.False(deactivated!.IsActive);
        Assert.Single(await harness.Products.ListAsync(new CatalogProductListQuery(null, null, false), CancellationToken.None));
    }

    [Fact]
    public async Task Portal_create_order_snapshots_and_allocates_number()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 10m, Code = "A1" },
            CancellationToken.None);
        var customer = harness.AddCustomer();
        await harness.Db.SaveChangesAsync();

        var order = await harness.Portal.CreateOrderAsync(
            customer.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 2 }],
                CustomerNote = "urgente",
            },
            CancellationToken.None);

        Assert.Equal("#1", order.DisplayNumber);
        Assert.Equal(CatalogOrderStatus.Requested, order.Status);
        Assert.Equal(20m, order.TotalAmount);
        Assert.Equal("Item", order.Items[0].ProductName);
        Assert.Equal(customer.Name, order.CustomerName);
        Assert.Contains(harness.Db.CatalogOrderStatusHistories, h => h.Status == CatalogOrderStatus.Requested);
    }

    [Fact]
    public async Task Orders_are_tenant_scoped_for_read_and_transition()
    {
        await using var harness = await CatalogHarness.CreateAsync();
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

        var otherProvider = new Fakes.FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var otherDb = Infrastructure.InMemoryAppDb.Create(otherProvider);
        otherDb.Tenants.Add(new Tenant("Other", "55555555000191"));
        await otherDb.SaveChangesAsync();
        harness.TenantProvider.TenantId = otherProvider.TenantId;

        Assert.Null(await harness.Orders.GetByIdAsync(order.Id, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Orders.ApproveAsync(order.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Internal_file_url_is_tenant_scoped()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);
        var pdf = "%PDF-1.4 dummy"u8.ToArray();
        await using var privateStream = new MemoryStream(pdf);
        var file = await harness.Products.AddFileAsync(
            product.Id,
            "tech.pdf",
            "application/pdf",
            privateStream,
            CatalogFileVisibility.InternalB2B,
            CancellationToken.None);

        var otherProvider = new Fakes.FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var otherDb = Infrastructure.InMemoryAppDb.Create(otherProvider);
        otherDb.Tenants.Add(new Tenant("Other", "55555555000191"));
        await otherDb.SaveChangesAsync();
        harness.TenantProvider.TenantId = otherProvider.TenantId;

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => harness.Products.GetFileUrlAsync(product.Id, file.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Portal_cannot_see_another_customer_order()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);
        var owner = harness.AddCustomer("Owner");
        var other = harness.AddCustomer("Other");
        await harness.Db.SaveChangesAsync();
        var order = await harness.Portal.CreateOrderAsync(
            owner.Id,
            new CreatePortalOrderRequest
            {
                Items = [new CreatePortalOrderItemRequest { ProductId = product.Id, Quantity = 1 }],
            },
            CancellationToken.None);

        Assert.Null(await harness.Portal.GetOrderAsync(other.Id, order.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Customer_cannot_cancel_approved_order()
    {
        await using var harness = await CatalogHarness.CreateAsync();
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
        await harness.Orders.ApproveAsync(order.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(
            () => harness.Portal.CancelOrderAsync(customer.Id, order.Id, CancellationToken.None));
    }

    [Fact]
    public async Task B2B_reject_requires_reason_and_invalid_transition_is_conflict()
    {
        await using var harness = await CatalogHarness.CreateAsync();
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

        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Orders.RejectAsync(order.Id, "  ", CancellationToken.None));
        await harness.Orders.ApproveAsync(order.Id, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidCatalogOrderTransitionException>(
            () => harness.Orders.ApproveAsync(order.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Module_gate_rejects_when_catalog_disabled()
    {
        await using var harness = await CatalogHarness.CreateAsync(catalogEnabled: false);
        await Assert.ThrowsAsync<CatalogModuleInactiveException>(
            () => harness.Gate.EnsureActiveAsync());
    }

    [Fact]
    public async Task Portal_product_omits_internal_files()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var product = await harness.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m },
            CancellationToken.None);

        var png = PngBytes();
        await using var publicStream = new MemoryStream(png);
        await harness.Products.AddFileAsync(
            product.Id,
            "photo.png",
            "image/png",
            publicStream,
            CatalogFileVisibility.CustomerVisible,
            CancellationToken.None);

        var pdf = "%PDF-1.4 dummy"u8.ToArray();
        await using var privateStream = new MemoryStream(pdf);
        await harness.Products.AddFileAsync(
            product.Id,
            "tech.pdf",
            "application/pdf",
            privateStream,
            CatalogFileVisibility.InternalB2B,
            CancellationToken.None);

        var portal = await harness.Portal.GetProductAsync(product.Id, CancellationToken.None);
        Assert.Single(portal!.Files);
        Assert.Equal("photo.png", portal.Files[0].FileName);
        Assert.DoesNotContain("storageKey", portal.Files[0].Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sms_activate_is_rejected_and_queued_sms_is_permanent_failure()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Notifications.UpsertChannelConfigAsync(
                new UpsertCatalogChannelConfigRequest
                {
                    EventType = "catalog.order.created",
                    Channel = NotificationChannel.Sms,
                    IsActive = true,
                },
                CancellationToken.None));
        Assert.Equal("SMS channel is not available.", ex.Message);

        var tenantId = harness.Tenant.Id;
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = "catalog.order.created",
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Channel = NotificationChannel.Sms,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientPhone = "+5511999990000",
            Status = NotificationDeliveryStatus.Queued,
            NextAttemptAt = DateTimeOffset.UtcNow,
        };
        notification.AddDelivery(delivery);
        harness.Db.Notifications.Add(notification);
        await harness.Db.SaveChangesAsync();

        await harness.Processor.ProcessDeliveryAsync(delivery.Id, CancellationToken.None);
        var reloaded = await harness.Db.NotificationDeliveries.FindAsync(delivery.Id);
        Assert.Equal(NotificationDeliveryStatus.Failed, reloaded!.Status);
        Assert.Equal("SMS channel is not available.", reloaded.ErrorMessage);
        Assert.Contains(harness.Db.NotificationDeliveryAttempts, a => a.Outcome == NotificationAttemptOutcome.PermanentFailure);
    }

    [Fact]
    public async Task Resend_appends_attempt_on_same_delivery()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var tenantId = harness.Tenant.Id;
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = "catalog.order.created",
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?> { ["orderNumber"] = "#1", ["customerName"] = "Ana", ["tenantName"] = "Club" },
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Channel = NotificationChannel.Email,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientEmail = "ana@club.test",
            Status = NotificationDeliveryStatus.Failed,
            ErrorMessage = "boom",
            AttemptCount = 1,
        };
        delivery.AddAttempt(new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Outcome = NotificationAttemptOutcome.PermanentFailure,
        });
        notification.AddDelivery(delivery);
        harness.Db.Notifications.Add(notification);
        harness.Db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = "catalog.order.created",
            Channel = NotificationChannel.Email,
            BodyTemplate = "Pedido {{orderNumber}}",
            SubjectTemplate = "Pedido",
        });
        await harness.Db.SaveChangesAsync();

        var resent = await harness.Notifications.ResendAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(2, resent.AttemptCount);
        Assert.Equal(2, harness.Db.NotificationDeliveryAttempts.Count(a => a.DeliveryId == delivery.Id));
        Assert.Equal(NotificationDeliveryStatus.Sent, resent.Status);
    }

    [Fact]
    public async Task Process_is_idempotent_after_sent()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var tenantId = harness.Tenant.Id;
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = "catalog.order.created",
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?> { ["orderNumber"] = "#1" },
        };
        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Channel = NotificationChannel.Email,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientEmail = "ana@club.test",
            Status = NotificationDeliveryStatus.Queued,
            NextAttemptAt = DateTimeOffset.UtcNow,
        };
        notification.AddDelivery(delivery);
        harness.Db.Notifications.Add(notification);
        harness.Db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = "catalog.order.created",
            Channel = NotificationChannel.Email,
            BodyTemplate = "Pedido {{orderNumber}}",
            SubjectTemplate = "Pedido",
        });
        await harness.Db.SaveChangesAsync();

        await harness.Processor.ProcessDeliveryAsync(delivery.Id, CancellationToken.None);
        await harness.Processor.ProcessDeliveryAsync(delivery.Id, CancellationToken.None);
        Assert.Equal(1, harness.Db.NotificationDeliveryAttempts.Count(a => a.DeliveryId == delivery.Id));
    }

    [Fact]
    public async Task Created_order_notifies_customer_in_app_in_transaction()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        harness.AddManageUser();
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

        var inApp = harness.Db.NotificationDeliveries
            .Where(d => d.Channel == NotificationChannel.InApp)
            .ToList();
        Assert.NotEmpty(inApp);
        Assert.All(inApp, d => Assert.Equal(NotificationDeliveryStatus.Delivered, d.Status));
    }

    private static byte[] PngBytes()
    {
        var bytes = new byte[16];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;
        return bytes;
    }
}
