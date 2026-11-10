using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Notifications.Controllers;
using Platform.Api.Notifications;
using Platform.Api.Tests.Authorization;
using Platform.Api.Tests.Catalog;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Common;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Notifications;

public sealed class NotificationDeliveryLogTests
{
    [Fact]
    public async Task Page_returns_newest_first_and_the_next_page()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var log = Log(harness);
        var older = Delivery(harness, "catalog.order.created", NotificationChannel.Email, NotificationDeliveryStatus.Sent, "old");
        var middle = Delivery(harness, "catalog.order.created", NotificationChannel.Email, NotificationDeliveryStatus.Sent, "mid");
        var newest = Delivery(harness, "catalog.order.created", NotificationChannel.Email, NotificationDeliveryStatus.Failed, "new");
        SetCreatedAt(older, DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        SetCreatedAt(middle, DateTimeOffset.Parse("2026-11-02T00:00:00Z"));
        SetCreatedAt(newest, DateTimeOffset.Parse("2026-11-03T00:00:00Z"));
        await harness.Db.SaveChangesAsync();

        var page = await log.ListPageAsync(EmptyFilter(), page: 1, pageSize: 2, CancellationToken.None);
        var next = await log.ListPageAsync(EmptyFilter(), page: 2, pageSize: 2, CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal([newest.Id, middle.Id], page.Items.Select(item => item.Id));
        Assert.Equal([older.Id], next.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task Filters_limit_status_channel_event_and_period()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var log = Log(harness);
        var match = Delivery(harness, "catalog.order.created", NotificationChannel.WhatsApp, NotificationDeliveryStatus.Failed, "match");
        var otherStatus = Delivery(harness, "catalog.order.created", NotificationChannel.WhatsApp, NotificationDeliveryStatus.Sent, "sent");
        var otherChannel = Delivery(harness, "catalog.order.created", NotificationChannel.Email, NotificationDeliveryStatus.Failed, "email");
        var otherEvent = Delivery(harness, RentalEventTypes.ReservationConfirmed, NotificationChannel.WhatsApp, NotificationDeliveryStatus.Failed, "rentals");
        var outside = Delivery(harness, "catalog.order.created", NotificationChannel.WhatsApp, NotificationDeliveryStatus.Failed, "old");
        SetCreatedAt(match, DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        SetCreatedAt(otherStatus, DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        SetCreatedAt(otherChannel, DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        SetCreatedAt(otherEvent, DateTimeOffset.Parse("2026-11-10T12:00:00Z"));
        SetCreatedAt(outside, DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
        await harness.Db.SaveChangesAsync();

        var page = await log.ListPageAsync(
            new NotificationDeliveryFilter(
                DateTimeOffset.Parse("2026-11-01T00:00:00Z"),
                DateTimeOffset.Parse("2026-11-30T00:00:00Z"),
                "catalog.order.created",
                null,
                NotificationChannel.WhatsApp,
                NotificationDeliveryStatus.Failed),
            page: 1,
            pageSize: 20,
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(match.Id, item.Id);
        Assert.Equal("catalog.order.created", item.EventKey);
        Assert.Equal(PlatformModules.Catalog, item.Module);
        Assert.Equal(1, page.TotalCount);

        var rentals = await log.ListPageAsync(
            new NotificationDeliveryFilter(null, null, RentalEventTypes.ReservationConfirmed, null, null, null),
            1,
            20,
            CancellationToken.None);
        Assert.Equal(PlatformModules.Rentals, Assert.Single(rentals.Items).Module);

        var unknown = Delivery(harness, "notice.email", NotificationChannel.Email, NotificationDeliveryStatus.Queued, "plain");
        await harness.Db.SaveChangesAsync();
        var plain = await log.ListPageAsync(
            new NotificationDeliveryFilter(null, null, "notice.email", null, null, null),
            1,
            20,
            CancellationToken.None);
        Assert.Null(Assert.Single(plain.Items).Module);
        Assert.Equal("notice.email", plain.Items[0].EventKey);
    }

    [Fact]
    public async Task Catalog_wrapper_matches_the_core_delivery()
    {
        await using var harness = await CatalogHarness.CreateAsync();
        var log = Log(harness);
        var delivery = Delivery(harness, "catalog.order.created", NotificationChannel.Email, NotificationDeliveryStatus.Failed, "wrap");
        delivery.ErrorMessage = "boom";
        delivery.AddAttempt(new NotificationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.Parse("2026-11-09T00:00:00Z"),
            Outcome = NotificationAttemptOutcome.PermanentFailure,
            ErrorMessage = "boom",
        });
        harness.Db.NotificationTemplates.Add(new NotificationTemplate
        {
            EventType = "catalog.order.created",
            Channel = NotificationChannel.Email,
            BodyTemplate = "Pedido {{orderNumber}}",
            SubjectTemplate = "Pedido",
        });
        await harness.Db.SaveChangesAsync();

        var catalog = await harness.Notifications.ListAsync(
            new CatalogNotificationListQuery(null, null, null, null, null, null),
            CancellationToken.None);
        var core = await log.ListPageAsync(EmptyFilter(), 1, 20, CancellationToken.None);
        var catalogRow = Assert.Single(catalog);
        var coreRow = Assert.Single(core.Items);
        Assert.Equal(catalogRow.Id, coreRow.Id);
        Assert.Equal(catalogRow.EventType, coreRow.EventKey);
        Assert.Equal(catalogRow.Status, coreRow.Status);
        Assert.Equal(catalogRow.ErrorMessage, coreRow.ErrorMessage);
        Assert.Equal(catalogRow.CanResend, coreRow.CanResend);
        Assert.Equal(catalogRow.Attempts.Single().AttemptNumber, coreRow.Attempts.Single().AttemptNumber);
        Assert.Equal(catalogRow.Attempts.Single().ErrorMessage, coreRow.Attempts.Single().ErrorMessage);
        Assert.Equal(NotificationChannel.Email, coreRow.Attempts.Single().Channel);

        var viaCatalog = await harness.Notifications.ResendAsync(delivery.Id, CancellationToken.None);
        var viaCore = await log.ListPageAsync(EmptyFilter(), 1, 20, CancellationToken.None);
        Assert.Equal(NotificationDeliveryStatus.Sent, viaCatalog.Status);
        Assert.Equal(viaCatalog.Status, Assert.Single(viaCore.Items).Status);
        Assert.Equal(viaCatalog.CanResend, viaCore.Items[0].CanResend);
        Assert.Equal(viaCatalog.Attempts.Count, viaCore.Items[0].Attempts.Count);
    }

    [Fact]
    public void Core_routes_use_core_permissions_and_do_not_require_catalog()
    {
        Assert.Null(typeof(NotificationDeliveriesController).GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Core.NotificationsRead),
            Policy(typeof(NotificationDeliveriesController).GetMethod(nameof(NotificationDeliveriesController.List))!));
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Core.NotificationsResend),
            Policy(typeof(NotificationDeliveriesController).GetMethod(nameof(NotificationDeliveriesController.Resend))!));
        Assert.NotNull(typeof(CatalogNotificationsController).GetCustomAttribute<RequireActiveModuleAttribute>());
        Assert.Equal(
            PermissionPolicies.Name(Permissions.Core.NotificationsWrite),
            Policy(typeof(CatalogNotificationsController).GetMethod(nameof(CatalogNotificationsController.UpsertChannel))!));
    }

    [Fact]
    public async Task Resend_permission_is_independent_of_the_catalog_grant()
    {
        await using var harness = await RbacResolverHarness.CreateAsync();
        harness.Db.TenantModules.Add(new TenantModule(harness.Tenant.Id, PlatformModules.Catalog, isActive: true));
        var catalogOnly = harness.AddCustomRole("CatalogResend", Permissions.Catalog.NotificationsResend);
        var coreOnly = harness.AddCustomRole("CoreResend", Permissions.Core.NotificationsResend);
        var reader = harness.AddUser("catalog-resend");
        var sender = harness.AddUser("core-resend");
        harness.Assign(reader, catalogOnly);
        harness.Assign(sender, coreOnly);
        await harness.Db.SaveChangesAsync();

        var catalogEffective = await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, reader.Id);
        var coreEffective = await harness.Resolver.GetEffectivePermissionsAsync(harness.Tenant.Id, sender.Id);
        Assert.Contains(Permissions.Catalog.NotificationsResend, catalogEffective);
        Assert.DoesNotContain(Permissions.Core.NotificationsResend, catalogEffective);
        Assert.Contains(Permissions.Core.NotificationsResend, coreEffective);
        Assert.DoesNotContain(Permissions.Core.NotificationsResend, PermissionCatalog.DefaultUserKeys);
        Assert.DoesNotContain(Permissions.Core.NotificationsResend, PermissionCatalog.TechnicianLegacyKeys);
    }

    [Fact]
    public void Resend_permission_seed_is_idempotent_and_copies_existing_catalog_grants()
    {
        var migration = new AddCoreNotificationResendPermission();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        Assert.Equal(AddCoreNotificationResendPermission.UpSql, up);
        Assert.Contains("ON CONFLICT (key) DO NOTHING", up, StringComparison.Ordinal);
        Assert.Equal(3, CountOf(up, "ON CONFLICT DO NOTHING"));
        Assert.Contains("source.key = 'catalog.notifications.resend'", up, StringComparison.Ordinal);
        Assert.Contains("added.key = 'core.notifications.resend'", up, StringComparison.Ordinal);
        Assert.Contains("added.key = 'core.notifications.write'", up, StringComparison.Ordinal);
        Assert.Contains("source.key = 'catalog.notifications.read'", up, StringComparison.Ordinal);
        Assert.Contains("added.key = 'core.notifications.read'", up, StringComparison.Ordinal);
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;
        Assert.Equal(AddCoreNotificationResendPermission.DownSql, down);
        Assert.Contains("key = 'core.notifications.resend'", down, StringComparison.Ordinal);
        Assert.DoesNotContain("core.notifications.read", down, StringComparison.Ordinal);
        Assert.DoesNotContain("core.notifications.write", down, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_an_invalid_page()
    {
        Assert.False(NotificationDeliveryPages.TryCreate(0, 20, out var error));
        Assert.Equal("Page must be at least 1.", error);
        Assert.False(NotificationDeliveryPages.TryCreate(1, 51, out error));
        Assert.Contains("50", error, StringComparison.Ordinal);
        Assert.False(NotificationDeliveryPages.TryCreate(int.MaxValue, 2, out error));
        Assert.Equal("Page is too large.", error);
        Assert.True(NotificationDeliveryPages.TryCreate(
            (int.MaxValue / NotificationDeliveryPages.MaxPageSize) + 1,
            NotificationDeliveryPages.MaxPageSize,
            out error));
        Assert.Equal(string.Empty, error);
    }

    private static NotificationDeliveryService Log(CatalogHarness harness) =>
        new(harness.Db, harness.TenantProvider, harness.Processor);

    private static NotificationDeliveryFilter EmptyFilter() =>
        new(null, null, null, null, null, null);

    private static NotificationDelivery Delivery(
        CatalogHarness harness,
        string eventType,
        NotificationChannel channel,
        NotificationDeliveryStatus status,
        string name)
    {
        var notification = new Notification
        {
            TenantId = harness.Tenant.Id,
            EventType = eventType,
            AggregateType = "Notice",
            AggregateId = Guid.NewGuid(),
            Payload = new Dictionary<string, string?>(),
        };
        var delivery = new NotificationDelivery
        {
            TenantId = harness.Tenant.Id,
            Notification = notification,
            NotificationId = notification.Id,
            Channel = channel,
            RecipientKind = NotificationRecipientKind.Customer,
            RecipientName = name,
            RecipientEmail = "ana@club.test",
            Status = status,
            ErrorMessage = status == NotificationDeliveryStatus.Failed ? "boom" : null,
        };
        notification.AddDelivery(delivery);
        harness.Db.Notifications.Add(notification);
        return delivery;
    }

    private static void SetCreatedAt(Entity entity, DateTimeOffset at) =>
        typeof(Entity).GetProperty(nameof(Entity.CreatedAt))!.SetValue(entity, at);

    private static string Policy(MethodInfo method) =>
        Assert.Single(method.GetCustomAttributes<AuthorizeAttribute>()).Policy!;

    private static int CountOf(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}

public sealed class NotificationGrantCopyTests : IClassFixture<PostgresContainerFixture>
{
    private readonly PostgresContainerFixture _postgres;

    public NotificationGrantCopyTests(PostgresContainerFixture postgres)
    {
        _postgres = postgres;
    }

    [DockerFact]
    public async Task Copies_catalog_read_and_resend_grants_once()
    {
        var factory = _postgres.Factory ?? throw new InvalidOperationException("PostgreSQL fixture was not initialized.");
        var tenantProvider = new FakeTenantProvider();
        Guid resendRoleId;
        Guid readRoleId;
        Guid bothRoleId;
        Guid alreadyRoleId;
        Guid untouchedRoleId;

        await using (var db = factory.Create(tenantProvider))
        {
            var tenant = new Tenant("Grant Club", Random.Shared.NextInt64(0, 100_000_000_000_000).ToString("D14"));
            var resendRole = new Role(tenant.Id, "Catalog resend");
            var readRole = new Role(tenant.Id, "Catalog read");
            var bothRole = new Role(tenant.Id, "Catalog both");
            var alreadyRole = new Role(tenant.Id, "Already write");
            var untouchedRole = new Role(tenant.Id, "No catalog grant");
            db.Tenants.Add(tenant);
            db.Roles.AddRange(resendRole, readRole, bothRole, alreadyRole, untouchedRole);
            await db.SaveChangesAsync();

            var permissions = await db.Permissions
                .Where(permission => permission.Key == Permissions.Catalog.NotificationsRead
                    || permission.Key == Permissions.Catalog.NotificationsResend
                    || permission.Key == Permissions.Core.NotificationsRead
                    || permission.Key == Permissions.Core.NotificationsWrite
                    || permission.Key == Permissions.Core.NotificationsResend)
                .ToListAsync();
            Guid Id(string key) => permissions.Single(permission => permission.Key == key).Id;

            db.RolePermissions.AddRange(
                new RolePermission(resendRole.Id, Id(Permissions.Catalog.NotificationsResend)),
                new RolePermission(readRole.Id, Id(Permissions.Catalog.NotificationsRead)),
                new RolePermission(bothRole.Id, Id(Permissions.Catalog.NotificationsRead)),
                new RolePermission(bothRole.Id, Id(Permissions.Catalog.NotificationsResend)),
                new RolePermission(alreadyRole.Id, Id(Permissions.Catalog.NotificationsResend)),
                new RolePermission(alreadyRole.Id, Id(Permissions.Core.NotificationsWrite)));
            await db.SaveChangesAsync();
            resendRoleId = resendRole.Id;
            readRoleId = readRole.Id;
            bothRoleId = bothRole.Id;
            alreadyRoleId = alreadyRole.Id;
            untouchedRoleId = untouchedRole.Id;
        }

        await using (var db = factory.Create(tenantProvider))
        {
            await db.Database.ExecuteSqlRawAsync(AddCoreNotificationResendPermission.UpSql);
            await db.Database.ExecuteSqlRawAsync(AddCoreNotificationResendPermission.UpSql);
        }

        await using var verify = factory.Create(tenantProvider);
        var grants = await verify.RolePermissions
            .Where(grant => grant.RoleId == resendRoleId
                || grant.RoleId == readRoleId
                || grant.RoleId == bothRoleId
                || grant.RoleId == alreadyRoleId
                || grant.RoleId == untouchedRoleId)
            .Select(grant => new { grant.RoleId, grant.Permission.Key })
            .ToListAsync();

        string[] Of(Guid roleId) => grants.Where(grant => grant.RoleId == roleId).Select(grant => grant.Key).Order().ToArray();

        Assert.Equal(
            [Permissions.Catalog.NotificationsResend, Permissions.Core.NotificationsResend, Permissions.Core.NotificationsWrite],
            Of(resendRoleId));
        Assert.Equal(
            [Permissions.Catalog.NotificationsRead, Permissions.Core.NotificationsRead],
            Of(readRoleId));
        Assert.Equal(
            [
                Permissions.Catalog.NotificationsRead,
                Permissions.Catalog.NotificationsResend,
                Permissions.Core.NotificationsRead,
                Permissions.Core.NotificationsResend,
                Permissions.Core.NotificationsWrite,
            ],
            Of(bothRoleId));
        Assert.Equal(
            [Permissions.Catalog.NotificationsResend, Permissions.Core.NotificationsResend, Permissions.Core.NotificationsWrite],
            Of(alreadyRoleId));
        Assert.Equal(1, grants.Count(grant => grant.RoleId == alreadyRoleId && grant.Key == Permissions.Core.NotificationsWrite));
        Assert.Empty(Of(untouchedRoleId));
    }
}
