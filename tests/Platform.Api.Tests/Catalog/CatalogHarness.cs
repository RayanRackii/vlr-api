using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Notifications;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

internal sealed class CatalogHarness : IAsyncDisposable
{
    private CatalogHarness(
        AppDbContext db,
        FakeTenantProvider tenantProvider,
        Tenant tenant,
        CatalogProductService products,
        CatalogOrderService orders,
        CatalogPortalService portal,
        CatalogNotificationService notifications,
        CatalogNotificationPublisher publisher,
        NotificationOutboxProcessor processor,
        RecordingOutboxScheduler scheduler,
        FakePermissionResolver permissionResolver)
    {
        Db = db;
        TenantProvider = tenantProvider;
        Tenant = tenant;
        Products = products;
        Orders = orders;
        Portal = portal;
        Notifications = notifications;
        Publisher = publisher;
        Processor = processor;
        Scheduler = scheduler;
        PermissionResolver = permissionResolver;
    }

    public AppDbContext Db { get; }

    public FakeTenantProvider TenantProvider { get; }

    public Tenant Tenant { get; }

    public CatalogProductService Products { get; }

    public CatalogOrderService Orders { get; }

    public CatalogPortalService Portal { get; }

    public CatalogNotificationService Notifications { get; }

    public CatalogNotificationPublisher Publisher { get; }

    public NotificationOutboxProcessor Processor { get; }

    public RecordingOutboxScheduler Scheduler { get; }

    public FakePermissionResolver PermissionResolver { get; }

    public static async Task<CatalogHarness> CreateAsync(bool catalogEnabled = true)
    {
        var tenant = new Tenant("Catalog Club", "44444444000191", subdomain: $"cat-{Guid.NewGuid():N}"[..12]);
        var tenantProvider = new FakeTenantProvider { TenantId = tenant.Id };
        var db = InMemoryAppDb.Create(tenantProvider);
        db.Tenants.Add(tenant);
        db.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Catalog, catalogEnabled));
        await db.SaveChangesAsync();

        var storageOptions = Options.Create(new StorageOptions());
        var storage = new DevStorageProvider(new FakeHostEnvironment(), storageOptions);
        var permissions = new FakePermissionResolver();
        var publisher = new CatalogNotificationPublisher(db, tenantProvider, permissions);
        var scheduler = new RecordingOutboxScheduler();
        var email = new DevEmailRecorder();
        var whatsApp = new DevWhatsAppRecorder();
        var processor = new NotificationOutboxProcessor(
            db,
            email,
            whatsApp,
            NullLogger<NotificationOutboxProcessor>.Instance);
        var products = new CatalogProductService(db, tenantProvider, storage, storageOptions);
        var http = new FakeHttpContextAccessor();
        var orders = new CatalogOrderService(db, tenantProvider, http, publisher, scheduler);
        var portal = new CatalogPortalService(
            db,
            tenantProvider,
            storage,
            storageOptions,
            publisher,
            scheduler);
        var notifications = new CatalogNotificationService(db, tenantProvider, publisher, processor);

        return new CatalogHarness(
            db,
            tenantProvider,
            tenant,
            products,
            orders,
            portal,
            notifications,
            publisher,
            processor,
            scheduler,
            permissions);
    }

    public Customer AddCustomer(string name = "Ana", Guid? id = null)
    {
        var customer = new Customer
        {
            TenantId = Tenant.Id,
            Name = name,
            Email = $"{Guid.NewGuid():N}@club.test",
            Phone = "+5511999990000",
        };
        Db.Customers.Add(customer);
        return customer;
    }

    public User AddManageUser(string name = "Ops")
    {
        var user = new User(Tenant.Id, Guid.NewGuid().ToString("N"), name, $"{Guid.NewGuid():N}@ops.test");
        Db.Users.Add(user);
        PermissionResolver.Grant(user.Id, Platform.Core.Domain.Constants.Permissions.Catalog.OrdersManage);
        return user;
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}

internal sealed class FakePermissionResolver : IPermissionResolver
{
    private readonly Dictionary<Guid, HashSet<string>> _grants = [];

    public void Grant(Guid userId, params string[] keys) =>
        _grants[userId] = keys.ToHashSet(StringComparer.Ordinal);

    public Task<IReadOnlySet<string>> GetEffectivePermissionsAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlySet<string> set = _grants.TryGetValue(userId, out var keys)
            ? keys
            : new HashSet<string>(StringComparer.Ordinal);
        return Task.FromResult(set);
    }

    public async Task<bool> HasPermissionAsync(
        Guid tenantId,
        Guid userId,
        string permissionKey,
        CancellationToken cancellationToken = default)
    {
        var effective = await GetEffectivePermissionsAsync(tenantId, userId, cancellationToken);
        return effective.Contains(permissionKey);
    }

    public Task<IReadOnlySet<string>> GetEnabledCatalogKeysAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}

internal sealed class RecordingOutboxScheduler : INotificationOutboxScheduler
{
    public List<Guid> Scheduled { get; } = [];

    public void Schedule(Guid deliveryId) => Scheduled.Add(deliveryId);
}

internal sealed class DevEmailRecorder : IEmailProvider
{
    public int SendCount { get; private set; }

    public Task SendAsync(
        string recipient,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        SendCount++;
        return Task.CompletedTask;
    }
}

internal sealed class DevWhatsAppRecorder : IWhatsAppProvider
{
    public int SendCount { get; private set; }

    public Task SendAsync(
        string recipient,
        string body,
        CancellationToken cancellationToken = default)
    {
        SendCount++;
        return Task.CompletedTask;
    }

    public Task SendTemplateAsync(
        string recipient,
        string templateName,
        string languageCode,
        IReadOnlyList<string> bodyParameters,
        CancellationToken cancellationToken = default)
    {
        SendCount++;
        return Task.CompletedTask;
    }
}
