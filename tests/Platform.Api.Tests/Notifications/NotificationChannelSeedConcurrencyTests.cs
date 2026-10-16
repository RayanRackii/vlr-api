using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Modules.Rentals.Services;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationChannelSeedConcurrencyTests : IClassFixture<PostgresContainerFixture>
{
    private static readonly NotificationChannel[] SeededChannels =
    [
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.WhatsApp,
        NotificationChannel.Sms,
    ];

    private readonly PostgresContainerFixture _postgres;

    public NotificationChannelSeedConcurrencyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Two_concurrent_rentals_seeds_keep_one_row_per_channel_and_email_active()
    {
        var factory = _postgres.Factory;
        Assert.NotNull(factory);

        for (var iteration = 0; iteration < 4; iteration++)
        {
            var tenantId = await SeedTenantAsync(factory);

            await Task.WhenAll(
                SeedChannelsAsync(factory, tenantId),
                SeedChannelsAsync(factory, tenantId));

            await using var verify = factory.Create(new FakeTenantProvider { TenantId = null });
            var rows = await verify.TenantNotificationChannelConfigs
                .IgnoreQueryFilters()
                .Where(config => config.TenantId == tenantId)
                .ToListAsync();

            var expected = RentalEventTypes.Notifying
                .SelectMany(eventType => SeededChannels.Select(channel => (eventType, channel)))
                .ToHashSet();
            Assert.Equal(expected, rows.Select(row => (row.EventType, row.Channel)).ToHashSet());
            Assert.Equal(expected.Count, rows.Count);
            Assert.All(
                rows.Where(row => row.Channel == NotificationChannel.Email),
                row => Assert.True(row.IsActive));
            Assert.All(
                rows.Where(row => row.Channel is NotificationChannel.WhatsApp or NotificationChannel.Sms),
                row => Assert.False(row.IsActive));
        }
    }

    private static async Task<Guid> SeedTenantAsync(PostgresAppDbFactory factory)
    {
        var tenantProvider = new FakeTenantProvider();
        await using var db = factory.Create(tenantProvider);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant(
            "Clube Seed",
            suffix[..14],
            subdomain: $"seed-{suffix}"[..20]);
        tenantProvider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task SeedChannelsAsync(PostgresAppDbFactory factory, Guid tenantId)
    {
        var tenantProvider = new FakeTenantProvider { TenantId = tenantId };
        await using var db = factory.Create(tenantProvider);
        var publisher = new RentalsNotificationPublisher(
            db,
            tenantProvider,
            NullLogger<RentalsNotificationPublisher>.Instance);
        await publisher.EnsureReadyAsync(CancellationToken.None);
    }
}
