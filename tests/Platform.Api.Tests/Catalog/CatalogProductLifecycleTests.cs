using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Controllers;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogProductLifecycleTests
{
    private static readonly StorageOptions Opts = new();

    private sealed record Ctx(
        CatalogHarness H,
        RecordingStorageProvider Storage,
        CatalogProductService Products,
        CatalogProductLifecycleService Lifecycle,
        CollectingLogger<CatalogProductLifecycleService> Log);

    private static async Task<Ctx> CreateAsync()
    {
        var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options);
        var log = new CollectingLogger<CatalogProductLifecycleService>();
        var lifecycle = new CatalogProductLifecycleService(h.Db, h.TenantProvider, storage, options, log);
        return new Ctx(h, storage, products, lifecycle, log);
    }

    private static async Task<CatalogProductResponse> NewProductAsync(Ctx c, string name = "Item", string? code = null) =>
        await c.Products.CreateAsync(
            new CreateCatalogProductRequest { Name = name, Code = code, Description = "Desc", Price = 12.5m },
            CancellationToken.None);

    private static async Task AddOrderForAsync(Ctx c, Guid productId)
    {
        var customer = c.H.AddCustomer();
        var order = new CatalogOrder
        {
            TenantId = c.H.Tenant.Id,
            CustomerId = customer.Id,
            OrderNumber = 1,
            CustomerNameSnapshot = customer.Name,
            Currency = "BRL",
        };
        order.AddItem(new CatalogOrderItem
        {
            TenantId = c.H.Tenant.Id,
            OrderId = order.Id,
            ProductId = productId,
            ProductNameSnapshot = "Item",
            Quantity = 1,
            UnitPriceSnapshot = 12.5m,
            SubTotal = 12.5m,
            Currency = "BRL",
        });
        c.H.Db.CatalogOrders.Add(order);
        await c.H.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task Duplicate_copies_fields_taxonomy_active_requirements_and_access_but_not_files()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var source = await NewProductAsync(c, "Camiseta", "CAM-1");
        var tenantId = c.H.Tenant.Id;

        var entity = await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == source.Id);
        entity.UsesSchedule = false;
        entity.Visibility = CatalogProductVisibility.Restricted;
        entity.ResourceRequirementsVersion = 2;

        var categoryId = Guid.NewGuid();
        var tagId = Guid.NewGuid();
        c.H.Db.CatalogProductCategories.Add(new CatalogProductCategory { TenantId = tenantId, ProductId = source.Id, CategoryId = categoryId });
        c.H.Db.CatalogProductTags.Add(new CatalogProductTag { TenantId = tenantId, ProductId = source.Id, TagId = tagId });

        var group = new CustomerGroup { TenantId = tenantId, Name = "VIP" };
        var customer = c.H.AddCustomer();
        c.H.Db.CustomerGroups.Add(group);
        c.H.Db.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenantId, ProductId = source.Id, GroupId = group.Id });
        c.H.Db.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenantId, ProductId = source.Id, CustomerId = customer.Id });

        var rentalAssetId = Guid.NewGuid();
        c.H.Db.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
        {
            TenantId = tenantId, ProductId = source.Id, RentalAssetId = rentalAssetId, ResourceQuantity = 2,
            DurationMinutesPerUnit = 45, PrepMinutes = 5, CleanupMinutes = 10, IsActive = true, Version = 2,
        });
        c.H.Db.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
        {
            TenantId = tenantId, ProductId = source.Id, RentalAssetId = Guid.NewGuid(), ResourceQuantity = 9,
            DurationMinutesPerUnit = 1, IsActive = false, Version = 1,
        });
        await c.H.Db.SaveChangesAsync();

        var pdf = "%PDF-1.4 dummy"u8.ToArray();
        await using (var stream = new MemoryStream(pdf))
        {
            await c.Products.AddFileAsync(source.Id, "doc.pdf", "application/pdf", stream,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        var copyId = await c.Lifecycle.DuplicateAsync(source.Id, CancellationToken.None);

        Assert.NotNull(copyId);
        c.H.Db.ChangeTracker.Clear();
        var copy = await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == copyId);
        Assert.Equal("Cópia de Camiseta", copy.Name);
        Assert.Equal("CAM-1-COPIA", copy.Code);
        Assert.False(copy.IsActive);
        Assert.Equal("Desc", copy.Description);
        Assert.Equal(12.5m, copy.Price);
        Assert.Equal("BRL", copy.Currency);
        Assert.False(copy.UsesSchedule);
        Assert.Equal(CatalogProductVisibility.Restricted, copy.Visibility);

        Assert.Equal(categoryId, (await c.H.Db.CatalogProductCategories.SingleAsync(x => x.ProductId == copyId)).CategoryId);
        Assert.Equal(tagId, (await c.H.Db.CatalogProductTags.SingleAsync(x => x.ProductId == copyId)).TagId);

        var grants = await c.H.Db.CatalogProductAccess.Where(a => a.ProductId == copyId).ToListAsync();
        Assert.Equal(2, grants.Count);
        Assert.Contains(grants, a => a.GroupId == group.Id);
        Assert.Contains(grants, a => a.CustomerId == customer.Id);

        var requirement = await c.H.Db.CatalogProductResourceRequirements.SingleAsync(r => r.ProductId == copyId);
        Assert.Equal(rentalAssetId, requirement.RentalAssetId);
        Assert.Equal(2, requirement.ResourceQuantity);
        Assert.Equal(45, requirement.DurationMinutesPerUnit);
        Assert.True(requirement.IsActive);
        Assert.Equal(copy.ResourceRequirementsVersion, requirement.Version);

        Assert.Empty(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == copyId).ToListAsync());
        Assert.Single(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == source.Id).ToListAsync());

        // The source is untouched.
        var original = await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == source.Id);
        Assert.True(original.IsActive);
        Assert.Equal("CAM-1", original.Code);
    }

    [Fact]
    public async Task Duplicate_picks_the_next_free_code()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var source = await NewProductAsync(c, "Item", "ABC");

        var first = await c.Lifecycle.DuplicateAsync(source.Id, CancellationToken.None);
        var second = await c.Lifecycle.DuplicateAsync(source.Id, CancellationToken.None);
        var third = await c.Lifecycle.DuplicateAsync(source.Id, CancellationToken.None);

        c.H.Db.ChangeTracker.Clear();
        async Task<string?> CodeOf(Guid? id) => (await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == id)).Code;
        Assert.Equal("ABC-COPIA", await CodeOf(first));
        Assert.Equal("ABC-COPIA2", await CodeOf(second));
        Assert.Equal("ABC-COPIA3", await CodeOf(third));
    }

    [Fact]
    public async Task Duplicate_trims_long_names_and_codes_and_keeps_a_missing_code_missing()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var longName = new string('n', 200);
        var longCode = new string('c', 80);
        var withLongValues = await NewProductAsync(c, longName, longCode);
        var withoutCode = await NewProductAsync(c, "Sem codigo");

        var longCopy = await c.Lifecycle.DuplicateAsync(withLongValues.Id, CancellationToken.None);
        var noCodeCopy = await c.Lifecycle.DuplicateAsync(withoutCode.Id, CancellationToken.None);

        c.H.Db.ChangeTracker.Clear();
        var a = await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == longCopy);
        Assert.True(a.Name.Length <= 200);
        Assert.StartsWith("Cópia de n", a.Name);
        Assert.True(a.Code!.Length <= 80);
        Assert.EndsWith("-COPIA", a.Code);
        Assert.NotEqual(longCode, a.Code);
        Assert.Null((await c.H.Db.CatalogProducts.SingleAsync(p => p.Id == noCodeCopy)).Code);
    }

    [Fact]
    public async Task Duplicate_and_delete_ignore_products_of_another_tenant()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var source = await NewProductAsync(c, "Item", "ABC");

        var home = c.H.TenantProvider.TenantId;
        c.H.TenantProvider.TenantId = Guid.NewGuid();
        var copy = await c.Lifecycle.DuplicateAsync(source.Id, CancellationToken.None);
        var deleted = await c.Lifecycle.DeleteAsync(source.Id, CancellationToken.None);
        c.H.TenantProvider.TenantId = home;

        Assert.Null(copy);
        Assert.False(deleted);
        c.H.Db.ChangeTracker.Clear();
        var all = await c.H.Db.CatalogProducts.IgnoreQueryFilters().ToListAsync();
        Assert.Equal(source.Id, Assert.Single(all).Id);
    }

    [Fact]
    public async Task Delete_removes_the_product_its_dependants_and_storage_objects()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var tenantId = c.H.Tenant.Id;
        var product = await NewProductAsync(c, "Item", "ABC");
        var other = await NewProductAsync(c, "Outro", "XYZ");

        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        await using (var photo = new MemoryStream(png))
        {
            await c.Products.AddFileAsync(product.Id, "photo.png", "image/png", photo,
                CatalogFileVisibility.CustomerVisible, CancellationToken.None);
        }

        await using (var doc = new MemoryStream("%PDF-1.4 dummy"u8.ToArray()))
        {
            await c.Products.AddFileAsync(product.Id, "doc.pdf", "application/pdf", doc,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        await using (var otherDoc = new MemoryStream("%PDF-1.4 dummy"u8.ToArray()))
        {
            await c.Products.AddFileAsync(other.Id, "keep.pdf", "application/pdf", otherDoc,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        var customer = c.H.AddCustomer();
        c.H.Db.CatalogProductCategories.Add(new CatalogProductCategory { TenantId = tenantId, ProductId = product.Id, CategoryId = Guid.NewGuid() });
        c.H.Db.CatalogProductTags.Add(new CatalogProductTag { TenantId = tenantId, ProductId = product.Id, TagId = Guid.NewGuid() });
        c.H.Db.CatalogProductAccess.Add(new CatalogProductAccess { TenantId = tenantId, ProductId = product.Id, CustomerId = customer.Id });
        c.H.Db.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
        {
            TenantId = tenantId, ProductId = product.Id, RentalAssetId = Guid.NewGuid(), ResourceQuantity = 1,
            DurationMinutesPerUnit = 30, IsActive = false, Version = 1,
        });
        c.H.Db.CatalogProductResourceRequirements.Add(new CatalogProductResourceRequirement
        {
            TenantId = tenantId, ProductId = product.Id, RentalAssetId = Guid.NewGuid(), ResourceQuantity = 1,
            DurationMinutesPerUnit = 30, IsActive = true, Version = 2,
        });
        c.H.Db.CatalogProductTags.Add(new CatalogProductTag { TenantId = tenantId, ProductId = other.Id, TagId = Guid.NewGuid() });
        await c.H.Db.SaveChangesAsync();
        var storedKeys = (await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == product.Id).ToListAsync())
            .Select(f => f.StorageKey).ToHashSet();
        Assert.Equal(2, storedKeys.Count);

        var deleted = await c.Lifecycle.DeleteAsync(product.Id, CancellationToken.None);

        Assert.True(deleted);
        c.H.Db.ChangeTracker.Clear();
        Assert.False(await c.H.Db.CatalogProducts.AnyAsync(p => p.Id == product.Id));
        Assert.Empty(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == product.Id).ToListAsync());
        Assert.Empty(await c.H.Db.CatalogProductCategories.Where(x => x.ProductId == product.Id).ToListAsync());
        Assert.Empty(await c.H.Db.CatalogProductTags.Where(x => x.ProductId == product.Id).ToListAsync());
        Assert.Empty(await c.H.Db.CatalogProductAccess.Where(a => a.ProductId == product.Id).ToListAsync());
        Assert.Empty(await c.H.Db.CatalogProductResourceRequirements.Where(r => r.ProductId == product.Id).ToListAsync());
        Assert.Equal(storedKeys, c.Storage.Deletes.Select(d => d.Key).ToHashSet());

        // Other products are untouched.
        Assert.True(await c.H.Db.CatalogProducts.AnyAsync(p => p.Id == other.Id));
        Assert.Single(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == other.Id).ToListAsync());
        Assert.Single(await c.H.Db.CatalogProductTags.Where(x => x.ProductId == other.Id).ToListAsync());
    }

    [Fact]
    public async Task Delete_is_blocked_when_an_order_references_the_product()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await NewProductAsync(c);
        await using (var doc = new MemoryStream("%PDF-1.4 dummy"u8.ToArray()))
        {
            await c.Products.AddFileAsync(product.Id, "doc.pdf", "application/pdf", doc,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        await AddOrderForAsync(c, product.Id);

        var ex = await Assert.ThrowsAsync<ProductHasOrdersException>(
            () => c.Lifecycle.DeleteAsync(product.Id, CancellationToken.None));

        Assert.Equal("PRODUCT_HAS_ORDERS", ProductHasOrdersException.Code);
        Assert.NotNull(ex);
        c.H.Db.ChangeTracker.Clear();
        Assert.True(await c.H.Db.CatalogProducts.AnyAsync(p => p.Id == product.Id));
        Assert.Single(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == product.Id).ToListAsync());
        Assert.Empty(c.Storage.Deletes);
    }

    [Fact]
    public async Task Delete_succeeds_and_logs_a_warning_when_storage_deletion_fails()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await NewProductAsync(c);
        await using (var doc = new MemoryStream("%PDF-1.4 dummy"u8.ToArray()))
        {
            await c.Products.AddFileAsync(product.Id, "doc.pdf", "application/pdf", doc,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        var key = (await c.H.Db.CatalogProductFiles.SingleAsync(f => f.ProductId == product.Id)).StorageKey;
        c.Storage.FailRequiredDelete = true;

        Assert.True(await c.Lifecycle.DeleteAsync(product.Id, CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        Assert.False(await c.H.Db.CatalogProducts.AnyAsync(p => p.Id == product.Id));
        Assert.Empty(await c.H.Db.CatalogProductFiles.Where(f => f.ProductId == product.Id).ToListAsync());
        var warning = Assert.Single(c.Log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(key, warning.Message);
        Assert.Contains(product.Id.ToString(), warning.Message);
        Assert.Contains(c.H.Tenant.Id.ToString(), warning.Message);
    }

    [Fact]
    public async Task Delete_removes_no_storage_object_when_the_database_save_fails()
    {
        var tenant = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var failing = new FailingSaveInterceptor();
        await using var db = InMemoryAppDb.Create(tenant, interceptors: [failing]);
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(db, tenant, storage, options);
        var lifecycle = new CatalogProductLifecycleService(
            db, tenant, storage, options, new CollectingLogger<CatalogProductLifecycleService>());
        var product = await products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await using (var doc = new MemoryStream("%PDF-1.4 dummy"u8.ToArray()))
        {
            await products.AddFileAsync(product.Id, "doc.pdf", "application/pdf", doc,
                CatalogFileVisibility.InternalB2B, CancellationToken.None);
        }

        failing.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifecycle.DeleteAsync(product.Id, CancellationToken.None));

        Assert.Empty(storage.Deletes);
        failing.Fail = false;
        db.ChangeTracker.Clear();
        Assert.True(await db.CatalogProducts.AnyAsync(p => p.Id == product.Id));
        Assert.Single(await db.CatalogProductFiles.Where(f => f.ProductId == product.Id).ToListAsync());
    }

    [Fact]
    public async Task Delete_of_an_unknown_product_returns_false()
    {
        var c = await CreateAsync();
        await using var _ = c.H;

        Assert.False(await c.Lifecycle.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await c.Lifecycle.DuplicateAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private sealed class FailingSaveInterceptor : SaveChangesInterceptor
    {
        public bool Fail { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Fail
                ? throw new InvalidOperationException("Simulated database failure.")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private sealed class CollectingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData(nameof(CatalogProductsController.Duplicate))]
    [InlineData(nameof(CatalogProductsController.Delete))]
    public void Duplicate_and_delete_require_products_manage(string action)
    {
        var method = typeof(CatalogProductsController).GetMethod(action)
            ?? throw new InvalidOperationException($"CatalogProductsController.{action} is missing.");

        Assert.Equal(
            PermissionPolicies.Name(Permissions.Catalog.ProductsManage),
            method.GetCustomAttribute<RequirePermissionAttribute>()!.Policy);
    }
}
