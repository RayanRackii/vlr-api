using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Api.Notifications;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Platform.Api.Tests.Notifications;

/// <summary>
/// End-to-end inbox behaviour against a real PostgreSQL schema: grouping, pagination, mark-read
/// variants, cross-user / cross-tenant isolation, and read-time permission re-checks.
/// </summary>
public sealed class MeNotificationsServiceTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private const string CatalogEvent = CatalogEventTypes.OrderCreated;
    private const string CatalogReadyEvent = CatalogEventTypes.OrderReady;

    [DockerFact]
    public async Task Summary_groups_unread_by_event_and_counts_only_permitted_groups()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#1"));
            SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#2"));
            SeedInApp(seed, tenant, user, CatalogReadyEvent, payload: Order("#1"));
            // Already read -> excluded from the unread summary.
            SeedInApp(seed, tenant, user, CatalogEvent, readAt: DateTimeOffset.UtcNow, payload: Order("#3"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenant));
        var service = CreateService(db, tenant, user, Permissions.Catalog.OrdersManage);

        var summary = await service.GetSummaryAsync(Principal(), CancellationToken.None);

        Assert.Equal(3, summary.UnreadTotal);
        Assert.Equal(2, summary.Groups.Count);
        var created = summary.Groups.Single(g => g.EventType == CatalogEvent);
        Assert.Equal(2, created.Count);
        Assert.Equal(PlatformModules.Catalog, created.Module);
        Assert.Equal("/pedidos?status=pending", created.TargetRoute);
        Assert.Contains(summary.Groups, g => g.EventType == CatalogReadyEvent && g.Count == 1);
        // Groups ordered by most recent activity first.
        Assert.True(summary.Groups[0].LatestAt >= summary.Groups[1].LatestAt);
    }

    [DockerFact]
    public async Task List_paginates_with_opaque_cursor_and_renders_localized_title()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            for (var i = 0; i < 5; i++)
            {
                SeedInApp(seed, tenant, user, CatalogEvent, payload: Order($"#{i}"));
                await seed.SaveChangesAsync();
            }
        }

        await using var db = factory.Create(TenantProvider(tenant));
        var service = CreateService(db, tenant, user, Permissions.Catalog.OrdersManage);

        var seen = new HashSet<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await service.ListAsync(Principal(), cursor, unreadOnly: false, limit: 2, language: "pt-BR", CancellationToken.None);
            Assert.True(page.Items.Count <= 2);
            foreach (var item in page.Items)
            {
                Assert.True(seen.Add(item.Id), "cursor paging returned a duplicate item");
                Assert.StartsWith("Pedido #", item.Title);
                Assert.Equal(PlatformModules.Catalog, item.Module);
            }

            cursor = page.NextCursor;
            pages++;
            Assert.True(pages <= 10, "pagination did not terminate");
        }
        while (cursor is not null);

        Assert.Equal(5, seen.Count);
    }

    [DockerFact]
    public async Task List_unreadOnly_excludes_read_items()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#1"));
            SeedInApp(seed, tenant, user, CatalogEvent, readAt: DateTimeOffset.UtcNow, payload: Order("#2"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenant));
        var service = CreateService(db, tenant, user, Permissions.Catalog.OrdersManage);

        var unread = await service.ListAsync(Principal(), null, unreadOnly: true, limit: 20, language: null, CancellationToken.None);
        Assert.Single(unread.Items);
        Assert.Null(unread.Items[0].ReadAt);

        var all = await service.ListAsync(Principal(), null, unreadOnly: false, limit: 20, language: null, CancellationToken.None);
        Assert.Equal(2, all.Items.Count);
    }

    [DockerFact]
    public async Task MarkRead_by_ids_eventType_and_all_only_affects_matching_unread()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        Guid createdA, readyB;

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            createdA = SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#1")).Id;
            SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#2"));
            readyB = SeedInApp(seed, tenant, user, CatalogReadyEvent, payload: Order("#3")).Id;
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenant));
        var service = CreateService(db, tenant, user, Permissions.Catalog.OrdersManage);

        var byIds = await service.MarkReadAsync(Principal(), new MarkNotificationsReadRequest([createdA], null, null), CancellationToken.None);
        Assert.Equal(1, byIds);
        // Marking the same id again is a no-op.
        Assert.Equal(0, await service.MarkReadAsync(Principal(), new MarkNotificationsReadRequest([createdA], null, null), CancellationToken.None));

        var byEvent = await service.MarkReadAsync(Principal(), new MarkNotificationsReadRequest(null, CatalogEvent, null), CancellationToken.None);
        Assert.Equal(1, byEvent); // only the remaining unread created-order delivery

        var remaining = await service.GetSummaryAsync(Principal(), CancellationToken.None);
        Assert.Equal(1, remaining.UnreadTotal); // the ready-order delivery is still unread
        Assert.Equal(readyB, Assert.Single((await service.ListAsync(Principal(), null, true, 20, null, CancellationToken.None)).Items).Id);

        var all = await service.MarkReadAsync(Principal(), new MarkNotificationsReadRequest(null, null, true), CancellationToken.None);
        Assert.Equal(1, all);
        Assert.Equal(0, (await service.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
    }

    [DockerFact]
    public async Task User_A_cannot_see_or_mark_user_B_notifications()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        Guid bDelivery;

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            SeedInApp(seed, tenant, userA, CatalogEvent, payload: Order("#A"));
            bDelivery = SeedInApp(seed, tenant, userB, CatalogEvent, payload: Order("#B")).Id;
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenant));
        var serviceA = CreateService(db, tenant, userA, Permissions.Catalog.OrdersManage);

        var list = await serviceA.ListAsync(Principal(), null, false, 20, null, CancellationToken.None);
        Assert.Single(list.Items);
        Assert.DoesNotContain(list.Items, i => i.Id == bDelivery);

        // A cannot mark B's delivery even by explicit id.
        Assert.Equal(0, await serviceA.MarkReadAsync(Principal(), new MarkNotificationsReadRequest([bDelivery], null, null), CancellationToken.None));

        await using var dbB = factory.Create(TenantProvider(tenant));
        var serviceB = CreateService(dbB, tenant, userB, Permissions.Catalog.OrdersManage);
        Assert.Equal(1, (await serviceB.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
    }

    [DockerFact]
    public async Task Tenant_A_cannot_see_tenant_B_notifications()
    {
        var factory = Factory();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var user = Guid.NewGuid(); // same recipient id across tenants to prove the tenant guard

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            SeedInApp(seed, tenantA, user, CatalogEvent, payload: Order("#A"));
            SeedInApp(seed, tenantB, user, CatalogEvent, payload: Order("#B1"));
            SeedInApp(seed, tenantB, user, CatalogEvent, payload: Order("#B2"));
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenantA));
        var serviceA = CreateService(db, tenantA, user, Permissions.Catalog.OrdersManage);

        Assert.Equal(1, (await serviceA.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
        // Mark-all only touches tenant A.
        Assert.Equal(1, await serviceA.MarkReadAsync(Principal(), new MarkNotificationsReadRequest(null, null, true), CancellationToken.None));

        await using var dbB = factory.Create(TenantProvider(tenantB));
        var serviceB = CreateService(dbB, tenantB, user, Permissions.Catalog.OrdersManage);
        Assert.Equal(2, (await serviceB.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
    }

    [DockerFact]
    public async Task Revoked_permission_hides_groups_and_items_at_read_time()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        Guid delivery;

        await using (var seed = factory.Create(NullProvider()))
        {
            await SeedTemplateAsync(seed);
            delivery = SeedInApp(seed, tenant, user, CatalogEvent, payload: Order("#1")).Id;
            await seed.SaveChangesAsync();
        }

        await using var db = factory.Create(TenantProvider(tenant));
        // No permission granted -> the catalog event is not in the allowed set.
        var service = CreateService(db, tenant, user, grantedPermission: null);

        Assert.Equal(0, (await service.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
        Assert.Empty((await service.ListAsync(Principal(), null, false, 20, null, CancellationToken.None)).Items);
        Assert.Equal(0, await service.MarkReadAsync(Principal(), new MarkNotificationsReadRequest([delivery], null, null), CancellationToken.None));
    }

    [DockerFact]
    public async Task Unknown_or_non_staff_user_gets_empty_inbox()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();

        await using var db = factory.Create(TenantProvider(tenant));
        // Actor with no resolved user id (e.g. not an active B2B staff member).
        var service = new MeNotificationsService(db, new StubActorAccessor(new RbacActor(tenant, null, false)), new FakePermissionResolver());

        Assert.Equal(0, (await service.GetSummaryAsync(Principal(), CancellationToken.None)).UnreadTotal);
        Assert.Empty((await service.ListAsync(Principal(), null, false, 20, null, CancellationToken.None)).Items);
    }

    [DockerFact]
    public async Task StaffRecipientResolver_returns_active_users_with_permission_only()
    {
        var factory = Factory();
        var tenant = new Tenant("Resolver", "55555555000191", subdomain: $"res-{Guid.NewGuid():N}"[..12]);
        var other = new Tenant("Other", "66666666000191", subdomain: $"oth-{Guid.NewGuid():N}"[..12]);

        var granted = new User(tenant.Id, Guid.NewGuid().ToString("N"), "Granted", "g@test.com");
        var noPerm = new User(tenant.Id, Guid.NewGuid().ToString("N"), "NoPerm", "n@test.com");
        var inactive = new User(tenant.Id, Guid.NewGuid().ToString("N"), "Inactive", "i@test.com");
        inactive.Deactivate();
        var foreign = new User(other.Id, Guid.NewGuid().ToString("N"), "Foreign", "f@test.com");

        await using (var seed = factory.Create(NullProvider()))
        {
            seed.Tenants.AddRange(tenant, other);
            seed.Users.AddRange(granted, noPerm, inactive, foreign);
            await seed.SaveChangesAsync();
        }

        var permissions = new FakePermissionResolver();
        permissions.Grant(granted.Id, Permissions.Rentals.ReservationsRead);
        permissions.Grant(inactive.Id, Permissions.Rentals.ReservationsRead);
        permissions.Grant(foreign.Id, Permissions.Rentals.ReservationsRead);

        await using var db = factory.Create(TenantProvider(tenant.Id));
        var resolver = new StaffRecipientResolver(db, permissions);

        var recipients = await resolver.ResolveAsync(tenant.Id, Permissions.Rentals.ReservationsRead, CancellationToken.None);

        var recipient = Assert.Single(recipients);
        Assert.Equal(granted.Id, recipient.UserId);
    }

    [DockerFact]
    public async Task Retention_job_deletes_read_over_30d_and_any_over_90d()
    {
        var factory = Factory();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();
        Guid freshUnread, freshRead, staleRead, ancientUnread;

        await using (var seed = factory.Create(NullProvider()))
        {
            freshUnread = SeedInApp(seed, tenant, user, CatalogEvent).Id;
            freshRead = SeedInApp(seed, tenant, user, CatalogEvent, readAt: DateTimeOffset.UtcNow).Id;
            staleRead = SeedInApp(seed, tenant, user, CatalogEvent, readAt: DateTimeOffset.UtcNow).Id;
            ancientUnread = SeedInApp(seed, tenant, user, CatalogEvent).Id;
            await seed.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            // Backdate: staleRead read 31 days ago; ancientUnread created 91 days ago.
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE core.notification_deliveries SET read_at = {0} WHERE id = {1}",
                now.AddDays(-31), staleRead);
            await seed.Database.ExecuteSqlRawAsync(
                "UPDATE core.notification_deliveries SET created_at = {0} WHERE id = {1}",
                now.AddDays(-91), ancientUnread);
        }

        await using var db = factory.Create(NullProvider());
        var job = new NotificationRetentionJob(db, NullLogger<NotificationRetentionJob>.Instance, TimeProvider.System);
        await job.ExecuteAsync(CancellationToken.None);

        await using var verify = factory.Create(NullProvider());
        var surviving = await verify.NotificationDeliveries.Select(d => d.Id).ToListAsync();
        Assert.Contains(freshUnread, surviving);
        Assert.Contains(freshRead, surviving);
        Assert.DoesNotContain(staleRead, surviving);
        Assert.DoesNotContain(ancientUnread, surviving);
    }

    private PostgresAppDbFactory Factory() =>
        postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not started.");

    private static FakeTenantProvider NullProvider() => new();

    private static FakeTenantProvider TenantProvider(Guid tenantId) => new() { TenantId = tenantId };

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity());

    private static MeNotificationsService CreateService(
        AppDbContext db,
        Guid tenantId,
        Guid userId,
        string? grantedPermission)
    {
        var permissions = new FakePermissionResolver();
        if (grantedPermission is not null)
        {
            permissions.Grant(userId, grantedPermission);
        }

        return new MeNotificationsService(db, new StubActorAccessor(new RbacActor(tenantId, userId, false)), permissions);
    }

    private static Dictionary<string, string?> Order(string orderNumber) =>
        new(StringComparer.Ordinal) { ["orderNumber"] = orderNumber };

    private static async Task SeedTemplateAsync(AppDbContext db)
    {
        if (!await db.NotificationTemplates.AnyAsync(t => t.EventType == CatalogEvent && t.Channel == NotificationChannel.InApp))
        {
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                EventType = CatalogEvent,
                Channel = NotificationChannel.InApp,
                Language = "pt-BR",
                BodyTemplate = "Pedido {{orderNumber}} recebido.",
                IsActive = true,
            });
        }

        if (!await db.NotificationTemplates.AnyAsync(t => t.EventType == CatalogReadyEvent && t.Channel == NotificationChannel.InApp))
        {
            db.NotificationTemplates.Add(new NotificationTemplate
            {
                EventType = CatalogReadyEvent,
                Channel = NotificationChannel.InApp,
                Language = "pt-BR",
                BodyTemplate = "Pedido {{orderNumber}} pronto.",
                IsActive = true,
            });
        }

        await db.SaveChangesAsync();
    }

    private static NotificationDelivery SeedInApp(
        AppDbContext db,
        Guid tenantId,
        Guid recipientId,
        string eventType,
        DateTimeOffset? readAt = null,
        Dictionary<string, string?>? payload = null)
    {
        var notification = new Notification
        {
            TenantId = tenantId,
            EventType = eventType,
            AggregateType = "CatalogOrder",
            AggregateId = Guid.NewGuid(),
            Payload = payload ?? new Dictionary<string, string?>(StringComparer.Ordinal),
        };

        var delivery = new NotificationDelivery
        {
            TenantId = tenantId,
            NotificationId = notification.Id,
            Channel = NotificationChannel.InApp,
            RecipientKind = NotificationRecipientKind.B2BUser,
            RecipientId = recipientId,
            Status = NotificationDeliveryStatus.Queued,
        };
        delivery.MarkDelivered();
        if (readAt is { } read)
        {
            delivery.MarkRead(read);
        }

        notification.AddDelivery(delivery);
        db.Notifications.Add(notification);
        return delivery;
    }

    private sealed class StubActorAccessor(RbacActor actor) : IRbacActorAccessor
    {
        public Task<RbacActor> GetAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
            Task.FromResult(actor);
    }
}
