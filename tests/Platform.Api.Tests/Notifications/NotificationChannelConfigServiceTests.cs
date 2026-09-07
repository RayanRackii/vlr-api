using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Modules.Notifications;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationChannelConfigServiceTests
{
    [Fact]
    public async Task Catalog_only_tenant_lists_catalog_channels_without_sms_or_rentals()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog);

        var groups = await harness.Service.ListAsync(CancellationToken.None);

        var catalog = Assert.Single(groups);
        Assert.Equal(PlatformModules.Catalog, catalog.Module);
        Assert.Equal(CatalogEventTypes.Notifying, catalog.Events.Select(item => item.EventType));
        Assert.Equal(
            [
                "catalog.events.orderCreated",
                "catalog.events.orderApproved",
                "catalog.events.orderReady",
                "catalog.events.orderRejected",
                "catalog.events.orderCancelledBySupplier",
            ],
            catalog.Events.Select(item => item.DisplayKey));

        foreach (var evt in catalog.Events)
        {
            Assert.Equal(
                [NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.WhatsApp],
                evt.Channels.Select(item => item.Channel));
            Assert.DoesNotContain(evt.Channels, item => item.Channel == NotificationChannel.Sms);

            var inApp = evt.Channels.Single(item => item.Channel == NotificationChannel.InApp);
            Assert.True(inApp.IsActive);
            Assert.False(inApp.Configurable);

            var email = evt.Channels.Single(item => item.Channel == NotificationChannel.Email);
            Assert.False(email.IsActive);
            Assert.True(email.Configurable);

            var whatsApp = evt.Channels.Single(item => item.Channel == NotificationChannel.WhatsApp);
            Assert.False(whatsApp.IsActive);
            Assert.True(whatsApp.Configurable);
        }
    }

    [Fact]
    public async Task Rentals_only_tenant_lists_whatsapp_channels_default_off()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Rentals);

        var groups = await harness.Service.ListAsync(CancellationToken.None);

        var rentals = Assert.Single(groups);
        Assert.Equal(PlatformModules.Rentals, rentals.Module);
        Assert.Equal(RentalEventTypes.Notifying, rentals.Events.Select(item => item.EventType));
        Assert.Equal(
            [
                "rentals.events.reservationPendingDeposit",
                "rentals.events.reservationConfirmed",
                "rentals.events.reservationCanceled",
                "rentals.events.reservationCompleted",
                "rentals.events.reservationReminder",
            ],
            rentals.Events.Select(item => item.DisplayKey));

        foreach (var evt in rentals.Events)
        {
            var channel = Assert.Single(evt.Channels);
            Assert.Equal(NotificationChannel.WhatsApp, channel.Channel);
            Assert.False(channel.IsActive);
            Assert.True(channel.Configurable);
        }
    }

    [Fact]
    public async Task Both_modules_list_catalog_then_rentals_groups()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog, PlatformModules.Rentals);

        var groups = await harness.Service.ListAsync(CancellationToken.None);

        Assert.Equal(2, groups.Count);
        Assert.Equal(PlatformModules.Catalog, groups[0].Module);
        Assert.Equal(PlatformModules.Rentals, groups[1].Module);
        Assert.Equal(CatalogEventTypes.Notifying.Length, groups[0].Events.Count);
        Assert.Equal(RentalEventTypes.Notifying.Length, groups[1].Events.Count);
    }

    [Fact]
    public async Task Enable_then_disable_reservation_confirmed_whatsapp_persists()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Rentals);

        var enabled = await harness.Service.UpsertAsync(
            Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, true),
            CancellationToken.None);
        Assert.True(enabled.IsActive);
        Assert.True(FindChannel(await harness.Service.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);

        var disabled = await harness.Service.UpsertAsync(
            Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, false),
            CancellationToken.None);
        Assert.False(disabled.IsActive);
        Assert.False(FindChannel(await harness.Service.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);
    }

    [Fact]
    public async Task Enable_then_disable_reservation_reminder_whatsapp_persists()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Rentals);

        await harness.Service.UpsertAsync(
            Request(RentalEventTypes.ReservationReminder, NotificationChannel.WhatsApp, true),
            CancellationToken.None);
        Assert.True(FindChannel(await harness.Service.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationReminder,
            NotificationChannel.WhatsApp).IsActive);

        await harness.Service.UpsertAsync(
            Request(RentalEventTypes.ReservationReminder, NotificationChannel.WhatsApp, false),
            CancellationToken.None);
        Assert.False(FindChannel(await harness.Service.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationReminder,
            NotificationChannel.WhatsApp).IsActive);
    }

    [Fact]
    public async Task Put_sms_is_rejected()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.UpsertAsync(
                Request(CatalogEventTypes.OrderCreated, NotificationChannel.Sms, true),
                CancellationToken.None));
        Assert.Equal("SMS channel is not available.", ex.Message);
    }

    [Fact]
    public async Task Put_unknown_event_is_rejected()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.UpsertAsync(
                Request("catalog.order.preparing", NotificationChannel.Email, true),
                CancellationToken.None));
        Assert.Equal("Unknown notification event.", ex.Message);
    }

    [Fact]
    public async Task Put_email_on_rentals_event_is_not_configurable()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Rentals);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.UpsertAsync(
                Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.Email, true),
                CancellationToken.None));
        Assert.Equal("Channel is not configurable for this event.", ex.Message);
    }

    [Fact]
    public async Task Put_in_app_on_unified_service_is_not_configurable()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Service.UpsertAsync(
                Request(CatalogEventTypes.OrderCreated, NotificationChannel.InApp, false),
                CancellationToken.None));
        Assert.Equal("Channel is not configurable for this event.", ex.Message);
    }

    [Fact]
    public async Task Put_rentals_event_when_catalog_only_is_module_inactive()
    {
        await using var harness = await Harness.CreateAsync(PlatformModules.Catalog);

        var ex = await Assert.ThrowsAsync<TenantModuleInactiveException>(
            () => harness.Service.UpsertAsync(
                Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, true),
                CancellationToken.None));
        Assert.Equal(RequireActiveModuleAttribute.InactiveModuleError, ex.Message);
    }

    [Fact]
    public async Task Tenant_b_cannot_see_or_update_tenant_a_configs()
    {
        var databaseName = $"notif-iso-{Guid.NewGuid():N}";
        var providerA = new FakeTenantProvider();
        var providerB = new FakeTenantProvider();

        await using var dbA = InMemoryAppDb.Create(providerA, databaseName);
        await using var dbB = InMemoryAppDb.Create(providerB, databaseName);

        var tenantA = SeedTenant(dbA, providerA, "Club A", "55555555000191", PlatformModules.Rentals);
        var tenantB = SeedTenant(dbB, providerB, "Club B", "55555555000192", PlatformModules.Rentals);
        await dbA.SaveChangesAsync();
        await dbB.SaveChangesAsync();

        var serviceA = CreateService(dbA, providerA);
        var serviceB = CreateService(dbB, providerB);

        await serviceA.UpsertAsync(
            Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, true),
            CancellationToken.None);

        Assert.True(FindChannel(await serviceA.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);
        Assert.False(FindChannel(await serviceB.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);

        await serviceB.UpsertAsync(
            Request(RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, false),
            CancellationToken.None);

        Assert.True(FindChannel(await serviceA.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);
        Assert.False(FindChannel(await serviceB.ListAsync(CancellationToken.None),
            PlatformModules.Rentals,
            RentalEventTypes.ReservationConfirmed,
            NotificationChannel.WhatsApp).IsActive);

        Assert.Equal(
            1,
            dbA.TenantNotificationChannelConfigs.IgnoreQueryFilters().Count(item =>
                item.TenantId == tenantA.Id
                && item.EventType == RentalEventTypes.ReservationConfirmed
                && item.Channel == NotificationChannel.WhatsApp
                && item.IsActive));
        Assert.Equal(
            0,
            dbB.TenantNotificationChannelConfigs.IgnoreQueryFilters().Count(item =>
                item.TenantId == tenantB.Id
                && item.EventType == RentalEventTypes.ReservationConfirmed
                && item.Channel == NotificationChannel.WhatsApp
                && item.IsActive));
    }

    private static UpsertNotificationChannelConfigRequest Request(
        string eventType,
        NotificationChannel channel,
        bool isActive) =>
        new()
        {
            EventType = eventType,
            Channel = channel,
            IsActive = isActive,
        };

    private static NotificationChannelStateResponse FindChannel(
        IReadOnlyList<NotificationChannelConfigGroupResponse> groups,
        string module,
        string eventType,
        NotificationChannel channel)
    {
        var group = Assert.Single(groups, item => item.Module == module);
        var evt = Assert.Single(group.Events, item => item.EventType == eventType);
        return Assert.Single(evt.Channels, item => item.Channel == channel);
    }

    private static Tenant SeedTenant(
        AppDbContext db,
        FakeTenantProvider provider,
        string name,
        string taxId,
        params string[] modules)
    {
        var tenant = new Tenant(name, taxId, subdomain: $"{name[0]}-{Guid.NewGuid():N}"[..12]);
        provider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        foreach (var module in modules)
        {
            db.TenantModules.Add(new TenantModule(tenant.Id, module, isActive: true));
        }

        return tenant;
    }

    private static NotificationChannelConfigService CreateService(
        AppDbContext db,
        FakeTenantProvider tenantProvider)
    {
        var permissions = new FakePermissionResolver();
        var catalogPublisher = new CatalogNotificationPublisher(db, tenantProvider, permissions);
        var rentalsPublisher = new RentalsNotificationPublisher(db, tenantProvider);
        var accessor = new TenantModuleAccessor(db, tenantProvider);
        return new NotificationChannelConfigService(
            db,
            tenantProvider,
            accessor,
            catalogPublisher,
            rentalsPublisher);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(AppDbContext db, NotificationChannelConfigService service)
        {
            Db = db;
            Service = service;
        }

        public AppDbContext Db { get; }

        public NotificationChannelConfigService Service { get; }

        public static async Task<Harness> CreateAsync(params string[] activeModules)
        {
            var tenantProvider = new FakeTenantProvider();
            var db = InMemoryAppDb.Create(tenantProvider);
            SeedTenant(db, tenantProvider, "Notif Club", UniqueTaxId(), activeModules);
            await db.SaveChangesAsync();
            return new Harness(db, CreateService(db, tenantProvider));
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();

        private static string UniqueTaxId() => $"{Guid.NewGuid():N}"[..14];
    }
}
