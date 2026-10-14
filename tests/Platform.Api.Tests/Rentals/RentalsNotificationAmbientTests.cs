using Microsoft.Extensions.Logging;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Rentals;

public sealed class RentalsNotificationAmbientTests
{
    [Fact]
    public async Task Reservation_event_with_matching_tenant_publishes_and_stamps_the_ambient_tenant()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Ambient Match", "16161616000191");
        db.Tenants.Add(tenant);
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        tenantProvider.TenantId = tenant.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenant.Id);

        var queued = await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        var notification = Assert.Single(db.Notifications.ToList());
        Assert.Equal(tenant.Id, notification.TenantId);
        Assert.Equal(reservation.Id, notification.AggregateId);
        var delivery = Assert.Single(db.NotificationDeliveries.ToList());
        Assert.Equal(tenant.Id, delivery.TenantId);
        Assert.Equal(NotificationChannel.Email, delivery.Channel);
        Assert.Equal(NotificationRecipientKind.Customer, delivery.RecipientKind);
        Assert.Equal(reservation.CustomerId, delivery.RecipientId);
        Assert.Equal("ana@club.test", delivery.RecipientEmail);
        Assert.Equal(queued, [delivery.Id]);
    }

    [Fact]
    public async Task Reservation_event_resolves_customer_email_when_the_navigation_is_not_loaded()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Email Lookup", "26262626000191");
        var customer = new Customer
        {
            TenantId = tenant.Id,
            Name = "Ana",
            Email = "ana@club.test",
        };
        db.Tenants.Add(tenant);
        db.Customers.Add(customer);
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        tenantProvider.TenantId = tenant.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenant.Id);
        reservation.CustomerId = customer.Id;
        reservation.Customer = null!;

        await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        var delivery = Assert.Single(db.NotificationDeliveries.ToList());
        Assert.Equal("ana@club.test", delivery.RecipientEmail);
        Assert.Equal(customer.Id, delivery.RecipientId);
    }

    [Fact]
    public async Task Reservation_event_does_not_copy_email_from_another_tenant()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Email Tenant", "27272727000191");
        var other = new Tenant("Other Tenant", "28282828000191");
        var foreignCustomer = new Customer
        {
            TenantId = other.Id,
            Name = "Outra",
            Email = "outra@club.test",
        };
        db.Tenants.AddRange(tenant, other);
        db.Customers.Add(foreignCustomer);
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        tenantProvider.TenantId = tenant.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenant.Id);
        reservation.CustomerId = foreignCustomer.Id;
        reservation.Customer = null!;

        await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        var delivery = Assert.Single(db.NotificationDeliveries.ToList());
        Assert.Null(delivery.RecipientEmail);
        Assert.Equal(foreignCustomer.Id, delivery.RecipientId);
    }

    [Fact]
    public async Task Reservation_event_with_null_ambient_does_not_publish()
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(tenantProvider);
        var tenant = new Tenant("Null Ambient", "17171717000191");
        db.Tenants.Add(tenant);
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenant.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenant.Id);

        tenantProvider.TenantId = null;
        var queued = await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        Assert.Empty(queued);
        Assert.Empty(db.Notifications.ToList());
        Assert.Empty(db.NotificationDeliveries.ToList());
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("ambient tenant is missing", warning.Message, StringComparison.Ordinal);
        Assert.Contains(RentalEventTypes.ReservationConfirmed, warning.Message, StringComparison.Ordinal);
        AssertPiiFree(warning.Message, reservation, tenant.Id);
    }

    [Fact]
    public async Task Reservation_event_with_mismatched_tenant_does_not_publish()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Guard A", "18181818000191");
        var tenantB = new Tenant("Guard B", "19191919000191");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantNotificationChannelConfigs.AddRange(
            new TenantNotificationChannelConfig
            {
                TenantId = tenantA.Id,
                EventType = RentalEventTypes.ReservationConfirmed,
                Channel = NotificationChannel.InApp,
                IsActive = true,
            },
            new TenantNotificationChannelConfig
            {
                TenantId = tenantB.Id,
                EventType = RentalEventTypes.ReservationConfirmed,
                Channel = NotificationChannel.Email,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        dbTenant.TenantId = null;
        ambient.TenantId = tenantA.Id;
        var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var publisher = new RentalsNotificationPublisher(
            db,
            ambient,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenantB.Id);

        var queued = await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        Assert.Empty(queued);
        Assert.Empty(db.Notifications.ToList());
        Assert.Empty(db.NotificationDeliveries.ToList());
        var warning = Assert.Single(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("does not match the ambient tenant", warning.Message, StringComparison.Ordinal);
        Assert.Contains(RentalEventTypes.ReservationConfirmed, warning.Message, StringComparison.Ordinal);
        AssertPiiFree(warning.Message, reservation, tenantA.Id, tenantB.Id);
    }

    [Fact]
    public async Task Reservation_channel_configs_are_limited_to_the_ambient_tenant_without_the_query_filter()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Filter A", "20202020000191");
        var tenantB = new Tenant("Filter B", "21212121000191");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantNotificationChannelConfigs.AddRange(
            new TenantNotificationChannelConfig
            {
                TenantId = tenantA.Id,
                EventType = RentalEventTypes.ReservationConfirmed,
                Channel = NotificationChannel.Email,
                IsActive = true,
            },
            new TenantNotificationChannelConfig
            {
                TenantId = tenantB.Id,
                EventType = RentalEventTypes.ReservationConfirmed,
                Channel = NotificationChannel.InApp,
                IsActive = true,
            });
        await db.SaveChangesAsync();

        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenantA.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.WhatsApp,
            IsActive = true,
        });

        dbTenant.TenantId = null;
        ambient.TenantId = tenantB.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            ambient,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());
        var reservation = ReservationFor(tenantB.Id);

        await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationConfirmed);
        await db.SaveChangesAsync();

        var notification = Assert.Single(db.Notifications.ToList());
        Assert.Equal(tenantB.Id, notification.TenantId);
        var delivery = Assert.Single(db.NotificationDeliveries.ToList());
        Assert.Equal(NotificationChannel.InApp, delivery.Channel);
        Assert.Equal(tenantB.Id, delivery.TenantId);
        Assert.Equal(NotificationRecipientKind.Customer, delivery.RecipientKind);
        Assert.Equal(reservation.CustomerId, delivery.RecipientId);
    }

    [Fact]
    public async Task Reminder_from_another_tenant_does_not_suppress_the_ambient_publish()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Reminder A", "22222222000191");
        var tenantB = new Tenant("Reminder B", "23232323000191");
        db.Tenants.AddRange(tenantA, tenantB);
        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenantB.Id,
            EventType = RentalEventTypes.ReservationReminder,
            Channel = NotificationChannel.InApp,
            IsActive = true,
        });
        await db.SaveChangesAsync();

        var reservation = ReservationFor(tenantB.Id);
        db.Notifications.Add(new Notification
        {
            TenantId = tenantA.Id,
            EventType = RentalEventTypes.ReservationReminder,
            AggregateType = "Reservation",
            AggregateId = reservation.Id,
        });
        await db.SaveChangesAsync();

        dbTenant.TenantId = null;
        ambient.TenantId = tenantB.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            ambient,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());

        await publisher.PublishReservationEventAsync(
            reservation,
            RentalEventTypes.ReservationReminder);
        await db.SaveChangesAsync();

        var published = db.Notifications
            .Where(notification => notification.TenantId == tenantB.Id)
            .ToList();
        var notification = Assert.Single(published);
        Assert.Equal(reservation.Id, notification.AggregateId);
        var delivery = Assert.Single(db.NotificationDeliveries.Where(item => item.TenantId == tenantB.Id));
        Assert.Equal(NotificationChannel.InApp, delivery.Channel);
    }

    [Fact]
    public async Task Channel_defaults_seed_only_the_ambient_tenant_when_the_query_filter_is_off()
    {
        var dbTenant = new FakeTenantProvider();
        var ambient = new FakeTenantProvider();
        await using var db = InMemoryAppDb.Create(dbTenant);
        var tenantA = new Tenant("Seed A", "24242424000191");
        var tenantB = new Tenant("Seed B", "25252525000191");
        db.Tenants.AddRange(tenantA, tenantB);

        foreach (var eventType in RentalEventTypes.Notifying)
        {
            foreach (var channel in DefaultChannels)
            {
                if (eventType == RentalEventTypes.ReservationConfirmed
                    && channel == NotificationChannel.Email)
                {
                    continue;
                }

                db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
                {
                    TenantId = tenantA.Id,
                    EventType = eventType,
                    Channel = channel,
                    IsActive = false,
                });
            }
        }

        await db.SaveChangesAsync();

        var foreignBefore = db.TenantNotificationChannelConfigs
            .Where(config => config.TenantId == tenantA.Id)
            .Select(config => new { config.Id, config.EventType, config.Channel, config.IsActive })
            .ToList();

        db.TenantNotificationChannelConfigs.Add(new TenantNotificationChannelConfig
        {
            TenantId = tenantA.Id,
            EventType = RentalEventTypes.ReservationConfirmed,
            Channel = NotificationChannel.Email,
            IsActive = true,
        });

        dbTenant.TenantId = null;
        ambient.TenantId = tenantB.Id;
        using var loggerFactory = LoggerFactory.Create(_ => { });
        var publisher = new RentalsNotificationPublisher(
            db,
            ambient,
            loggerFactory.CreateLogger<RentalsNotificationPublisher>());

        await publisher.EnsureReadyAsync();

        var configs = db.TenantNotificationChannelConfigs.ToList();
        var foreign = configs.Where(config => config.TenantId == tenantA.Id).ToList();
        var seeded = configs.Where(config => config.TenantId == tenantB.Id).ToList();

        Assert.Equal(foreignBefore.Count + 1, foreign.Count);
        Assert.All(foreignBefore, before =>
        {
            var after = Assert.Single(
                foreign,
                config => config.Id == before.Id);
            Assert.Equal(before.EventType, after.EventType);
            Assert.Equal(before.Channel, after.Channel);
            Assert.Equal(before.IsActive, after.IsActive);
        });
        var foreignEmail = Assert.Single(
            foreign,
            config => config.EventType == RentalEventTypes.ReservationConfirmed
                && config.Channel == NotificationChannel.Email);
        Assert.True(foreignEmail.IsActive);
        Assert.Equal(RentalEventTypes.Notifying.Length * DefaultChannels.Length, seeded.Count);
        Assert.All(
            seeded.Where(config => config.Channel == NotificationChannel.InApp),
            config => Assert.True(config.IsActive));
        Assert.All(
            seeded.Where(config => config.Channel != NotificationChannel.InApp),
            config => Assert.False(config.IsActive));
        Assert.Contains(
            seeded,
            config => config.EventType == RentalEventTypes.ReservationConfirmed
                && config.Channel == NotificationChannel.Email
                && !config.IsActive);
        Assert.DoesNotContain(configs, config => config.TenantId != tenantA.Id && config.TenantId != tenantB.Id);
    }

    private static readonly NotificationChannel[] DefaultChannels =
    [
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.WhatsApp,
        NotificationChannel.Sms,
    ];

    private static Reservation ReservationFor(Guid tenantId)
    {
        var customerId = Guid.NewGuid();
        return new Reservation
        {
            TenantId = tenantId,
            UnitId = Guid.NewGuid(),
            CustomerId = customerId,
            CustomerName = "Ana",
            CustomerWhatsApp = "11988887777",
            StartDateTime = new DateTimeOffset(2026, 10, 20, 15, 0, 0, TimeSpan.Zero),
            EndDateTime = new DateTimeOffset(2026, 10, 20, 16, 0, 0, TimeSpan.Zero),
            Status = ReservationStatus.Confirmed,
            TotalAmount = 100m,
            DepositPaid = 0m,
            Customer = new Customer
            {
                TenantId = tenantId,
                Name = "Ana",
                Email = "ana@club.test",
                Phone = "11988887777",
            },
        };
    }

    private static void AssertPiiFree(string message, Reservation reservation, params Guid[] tenantIds)
    {
        Assert.DoesNotContain("Ana", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ana@club.test", message, StringComparison.Ordinal);
        Assert.DoesNotContain("11988887777", message, StringComparison.Ordinal);
        Assert.DoesNotContain(reservation.CustomerId.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain(reservation.Id.ToString(), message, StringComparison.Ordinal);
        foreach (var tenantId in tenantIds)
        {
            Assert.DoesNotContain(tenantId.ToString(), message, StringComparison.Ordinal);
        }
    }
}
