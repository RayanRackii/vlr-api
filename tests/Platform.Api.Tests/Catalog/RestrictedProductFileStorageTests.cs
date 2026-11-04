using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

/// <summary>Restricted products must never keep customer-visible files in the public bucket.</summary>
public sealed class RestrictedProductFileStorageTests
{
    private static readonly StorageOptions Opts = new();

    private sealed record Ctx(CatalogHarness H, RecordingStorageProvider Storage, CatalogProductService Products,
        CustomerGroupService Groups, CatalogPortalService Portal);

    private static async Task<Ctx> CreateAsync(ILogger<CustomerGroupService>? logger = null)
    {
        var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options);
        var groups = new CustomerGroupService(h.Db, h.TenantProvider, null, storage, options, logger);
        var portal = new CatalogPortalService(h.Db, h.TenantProvider, storage, options,
            h.Publisher.AsDomainEvents(), h.Scheduler, new Platform.Api.Modules.Rentals.Services.ResourceOccupancyService());
        return new Ctx(h, storage, products, groups, portal);
    }

    private static async Task<CatalogProductFileDto> AddPhotoAsync(Ctx c, Guid productId)
    {
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        await using var stream = new MemoryStream(png);
        return await c.Products.AddFileAsync(productId, "photo.png", "image/png", stream,
            CatalogFileVisibility.CustomerVisible, CancellationToken.None);
    }

    private sealed class RecordingLogger : ILogger<CustomerGroupService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task Opening_compensation_delete_failure_logs_error_and_rethrows_original_timeout()
    {
        var logger = new RecordingLogger();
        var c = await CreateAsync(logger);
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailAfterCopy = true;
        c.Storage.FailRequiredDelete = true;

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Public", null, null), CancellationToken.None));

        Assert.Equal("Copy timed out after storage accepted the object.", error.Message);
        var copy = Assert.Single(c.Storage.Copies);
        Assert.Contains((Opts.PublicBucket, copy.ToKey), c.Storage.Objects);
        Assert.Contains((Opts.PrivateBucket, copy.FromKey), c.Storage.Objects);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains(Opts.PublicBucket, entry.Message);
        Assert.Contains(copy.ToKey, entry.Message);
    }

    [Fact]
    public async Task Opening_timeout_after_copy_removes_public_destination_and_keeps_product_restricted()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailAfterCopy = true;

        await Assert.ThrowsAsync<TimeoutException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Public", null, null), CancellationToken.None));

        var copy = Assert.Single(c.Storage.Copies);
        Assert.DoesNotContain((Opts.PublicBucket, copy.ToKey), c.Storage.Objects);
        Assert.Contains((Opts.PrivateBucket, copy.FromKey), c.Storage.Objects);
        c.H.Db.ChangeTracker.Clear();
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Restricted, staff!.Visibility);
    }

    [Fact]
    public async Task Restricting_moves_customer_files_to_private_bucket_and_portal_signs_private_object()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Ana");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        var file = await AddPhotoAsync(c, product.Id);
        Assert.Contains(c.Storage.Uploads, u => u.Bucket == Opts.PublicBucket);
        Assert.NotNull(file.Url);

        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [customer.Id], null), CancellationToken.None);

        var copy = Assert.Single(c.Storage.Copies);
        Assert.Equal((Opts.PublicBucket, Opts.PrivateBucket), (copy.FromBucket, copy.ToBucket));
        Assert.Contains(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket && d.Key == copy.FromKey);

        var portal = await c.Portal.GetProductAsync(customer.Id, product.Id, CancellationToken.None);
        Assert.Single(portal!.Files);
        Assert.Contains(c.Storage.Signed, s => s.Bucket == Opts.PrivateBucket && s.Key == copy.FromKey);
        Assert.DoesNotContain(c.Storage.Signed, s => s.Bucket == Opts.PublicBucket);
        Assert.StartsWith("https://storage.test/sign/", portal.Files[0].Url);

        // Staff DTO no longer exposes a permanent public URL for this file.
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Null(staff!.Files.Single().Url);
        var signed = await c.Products.GetFileUrlAsync(product.Id, file.Id, CancellationToken.None);
        Assert.False(signed.IsPublic);
    }

    [Fact]
    public async Task New_upload_on_restricted_product_goes_private_and_reopening_moves_back()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        Assert.Empty(c.Storage.Copies);

        var file = await AddPhotoAsync(c, product.Id);
        Assert.Equal(Opts.PrivateBucket, Assert.Single(c.Storage.Uploads).Bucket);
        Assert.Null(file.Url);

        await c.Groups.SetProductAccessAsync(product.Id, new("Public", null, null), CancellationToken.None);
        var copy = Assert.Single(c.Storage.Copies);
        Assert.Equal((Opts.PrivateBucket, Opts.PublicBucket), (copy.FromBucket, copy.ToBucket));
        Assert.Contains(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
    }

    [Fact]
    public async Task Copy_failure_keeps_product_public_and_unchanged()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailCopy = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Public, staff!.Visibility);
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket);
    }

    [Fact]
    public async Task Public_copy_delete_failure_aborts_restriction_and_removes_private_copy()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailRequiredDelete = true;
        c.Storage.FailRequiredDeleteInBucket = Opts.PublicBucket;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Public, staff!.Visibility);
        Assert.Contains(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket);
    }

    [Fact]
    public async Task Public_restore_failure_keeps_private_copy_as_only_surviving_copy()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await AddPhotoAsync(c, product.Id);
        // First public delete succeeds (so its public copy is gone), the second fails -> compensation.
        // Restoring the deleted public copy then fails too, so that file's private copy is the only one left.
        c.Storage.FailRequiredDeleteAfter = 1;
        c.Storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
        c.Storage.FailCopyToBucket = Opts.PublicBucket;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Public, staff!.Visibility);

        // File whose public restore failed: its private copy is the only surviving copy -> must be kept.
        var restoreFailedKey = c.Storage.Copies[0].ToKey;
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket && d.Key == restoreFailedKey);
        // File whose public was never deleted: safe to drop its redundant private copy.
        var publicIntactKey = c.Storage.Copies[1].ToKey;
        Assert.Contains(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket && d.Key == publicIntactKey);
    }

    [Fact]
    public async Task Upload_picks_private_bucket_when_product_restricted_under_lock()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);

        var file = await AddPhotoAsync(c, product.Id);

        // Visibility is read after the product-row lock, so the bucket is the private one.
        Assert.Equal(Opts.PrivateBucket, Assert.Single(c.Storage.Uploads).Bucket);
        Assert.Null(file.Url);
    }

    [Fact]
    public async Task Restricting_deletes_public_copy_before_commit()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        var copy = Assert.Single(c.Storage.Copies);
        var publicDelete = Assert.Single(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket);
        Assert.Equal(copy.FromKey, publicDelete.Key);
    }
}
