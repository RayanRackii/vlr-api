using Microsoft.EntityFrameworkCore;
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

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Public")]
    public async Task Copy_timeout_after_success_is_verified_and_transition_completes(string target)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        if (target == "Public")
            await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailAfterCopy = true;

        await c.Groups.SetProductAccessAsync(product.Id, new(target, null, null), CancellationToken.None);

        c.H.Db.ChangeTracker.Clear();
        Assert.Equal(target, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        var copy = Assert.Single(c.Storage.Copies);
        Assert.Contains((copy.ToBucket, copy.ToKey), c.Storage.Objects);
        Assert.DoesNotContain((copy.FromBucket, copy.FromKey), c.Storage.Objects);
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
    public async Task Public_copy_delete_failure_reverts_to_public_and_keeps_private_copy()
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
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
        Assert.Contains(c.Storage.Objects, o => o.Bucket == Opts.PrivateBucket);
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_restore_failure_keeps_private_copy_as_only_surviving_copy(bool unverifiedCopy)
    {
        var logger = new RecordingLogger();
        var c = await CreateAsync(logger);
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await AddPhotoAsync(c, product.Id);
        // The first delete succeeds. Restore fails after the second delete forces a DB revert.
        c.Storage.FailRequiredDeleteAfter = 1;
        c.Storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
        if (unverifiedCopy) c.Storage.CopyWithoutPersistingToBucket = Opts.PublicBucket;
        else c.Storage.FailCopyToBucket = Opts.PublicBucket;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Equal(CatalogProductVisibility.Public, staff!.Visibility);

        // File whose public restore failed: its private copy is the only surviving copy -> must be kept.
        var restoreFailedKey = c.Storage.Copies[0].ToKey;
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket && d.Key == restoreFailedKey);
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData("Restricted")]
    [InlineData("Public")]
    public async Task Delete_timeout_after_success_is_confirmed_without_reverting(string target)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        if (target == "Public")
            await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.TimeoutAfterRequiredDeleteInBucket = target == "Public" ? Opts.PrivateBucket : Opts.PublicBucket;

        await c.Groups.SetProductAccessAsync(product.Id, new(target, null, null), CancellationToken.None);

        c.H.Db.ChangeTracker.Clear();
        Assert.Equal(target, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        var copy = Assert.Single(c.Storage.Copies);
        Assert.Contains((copy.ToBucket, copy.ToKey), c.Storage.Objects);
        Assert.DoesNotContain((copy.FromBucket, copy.FromKey), c.Storage.Objects);
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
    public async Task Restricting_deletes_public_copy_after_commit()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.BeforeRequiredDelete = (bucket, key) =>
        {
            Assert.Equal(CatalogProductVisibility.Restricted,
                c.H.Db.CatalogProducts.AsNoTracking().Single(p => p.Id == product.Id).Visibility);
            Assert.Contains((Opts.PrivateBucket, key), c.Storage.Objects);
        };
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        var copy = Assert.Single(c.Storage.Copies);
        var publicDelete = Assert.Single(c.Storage.Deletes, d => d.Bucket == Opts.PublicBucket);
        Assert.Equal(copy.FromKey, publicDelete.Key);
    }
    [Theory]
    [InlineData("Restricted", "copy")]
    [InlineData("Restricted", "verification")]
    [InlineData("Restricted", "exists")]
    [InlineData("Public", "copy")]
    [InlineData("Public", "verification")]
    [InlineData("Public", "exists")]
    [InlineData("Public", "partial")]
    public async Task Preparation_failure_keeps_original_visibility_and_all_source_files(string target, string failure)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var original = target == "Public" ? "Restricted" : "Public";
        if (original == "Restricted")
            await c.Groups.SetProductAccessAsync(product.Id, new(original, null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await AddPhotoAsync(c, product.Id);
        var destination = target == "Public" ? Opts.PublicBucket : Opts.PrivateBucket;
        if (failure == "copy") c.Storage.FailCopyToBucket = destination;
        if (failure == "partial") c.Storage.FailCopyAfter = 1;
        if (failure == "verification") c.Storage.CopyWithoutPersistingToBucket = destination;
        if (failure == "exists")
        {
            c.Storage.FailExistsInBucket = destination;
            c.Storage.FailExistsAfter = 1;
            c.Storage.FailExistsOnce = true;
        }

        await Assert.ThrowsAnyAsync<Exception>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new(target, null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        Assert.Equal(original, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        foreach (var upload in c.Storage.Uploads)
            Assert.Contains(upload, c.Storage.Objects);
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == c.Storage.Uploads[0].Bucket);
        if (original == "Restricted")
            Assert.DoesNotContain(c.Storage.Objects, o => o.Bucket == Opts.PublicBucket);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("unconfirmed")]
    [InlineData("exists")]
    [InlineData("restore-timeout")]
    public async Task Partial_public_cleanup_commits_revert_before_restoring_missing_public_copies(string failure)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Ana");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await AddPhotoAsync(c, product.Id);
        if (failure is "delete" or "restore-timeout")
        {
            c.Storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
            c.Storage.FailRequiredDeleteAfter = 1;
        }
        if (failure == "restore-timeout") c.Storage.FailAfterCopy = true;
        if (failure == "unconfirmed") c.Storage.IgnoreRequiredDeleteInBucket = Opts.PublicBucket;
        if (failure == "exists")
        {
            c.Storage.FailExistsInBucket = Opts.PublicBucket;
            c.Storage.FailExistsAfter = 1;
            c.Storage.FailExistsOnce = true;
        }
        c.Storage.BeforeCopy = (bucket, key) =>
        {
            if (bucket == Opts.PublicBucket)
                Assert.Equal(CatalogProductVisibility.Public,
                    c.H.Db.CatalogProducts.AsNoTracking().Single(p => p.Id == product.Id).Visibility);
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [customer.Id], null), CancellationToken.None));

        Assert.Contains("reverted to Public", error.Message);
        c.H.Db.ChangeTracker.Clear();
        var access = await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None);
        Assert.Equal("Public", access!.Visibility);
        Assert.Contains(customer.Id, access.CustomerIds);
        foreach (var upload in c.Storage.Uploads)
        {
            Assert.Contains((Opts.PublicBucket, upload.Key), c.Storage.Objects);
            Assert.Contains((Opts.PrivateBucket, upload.Key), c.Storage.Objects);
        }
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
    }

    [Fact]
    public async Task Missing_private_copy_prevents_deleting_last_public_copy()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        var checks = 0;
        c.Storage.BeforeExists = (bucket, key) =>
        {
            if (bucket == Opts.PrivateBucket && ++checks == 3)
                c.Storage.Objects.Remove((bucket, key));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None));

        Assert.Empty(c.Storage.Deletes);
        Assert.Contains(c.Storage.Uploads[0], c.Storage.Objects);
        Assert.Equal("Public", (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("copy")]
    [InlineData("verification")]
    [InlineData("exists")]
    public async Task Opening_preparation_and_public_cleanup_failure_recovers_public_and_retains_private_files(string restoreFailure)
    {
        var logger = new RecordingLogger();
        var c = await CreateAsync(logger);
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        for (var i = 0; i < 3; i++) await AddPhotoAsync(c, product.Id);
        c.Storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
        c.Storage.FailRequiredDeleteAfter = 1;
        var copies = 0;
        c.Storage.BeforeCopy = (bucket, key) =>
        {
            if (++copies != 3) return;
            c.Storage.BeforeCopy = (restoreBucket, restoreKey) =>
            {
                if (restoreFailure == "copy") c.Storage.FailCopyToBucket = Opts.PublicBucket;
                if (restoreFailure == "verification") c.Storage.CopyWithoutPersistingToBucket = Opts.PublicBucket;
                if (restoreFailure == "exists") c.Storage.FailExistsInBucket = Opts.PublicBucket;
            };
            throw new TimeoutException("Opening preparation failed after two public copies.");
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Public", null, null), CancellationToken.None));

        Assert.Equal("Public", (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        foreach (var upload in c.Storage.Uploads)
        {
            Assert.Contains((Opts.PrivateBucket, upload.Key), c.Storage.Objects);
            if (restoreFailure == "none") Assert.Contains((Opts.PublicBucket, upload.Key), c.Storage.Objects);
        }
        if (restoreFailure != "none")
            Assert.Contains(c.Storage.Uploads, upload => !c.Storage.Objects.Contains((Opts.PublicBucket, upload.Key)));
        Assert.DoesNotContain(c.Storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Private_cleanup_failure_fails_operation_but_keeps_committed_public_product_and_both_copies(bool copyTimeout)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        c.Storage.FailRequiredDelete = true;
        c.Storage.FailRequiredDeleteInBucket = Opts.PrivateBucket;
        c.Storage.FailAfterCopy = copyTimeout;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            c.Groups.SetProductAccessAsync(product.Id, new("Public", null, null), CancellationToken.None));

        c.H.Db.ChangeTracker.Clear();
        Assert.Equal("Public", (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        var copy = Assert.Single(c.Storage.Copies);
        Assert.Contains((Opts.PublicBucket, copy.ToKey), c.Storage.Objects);
        Assert.Contains((Opts.PrivateBucket, copy.FromKey), c.Storage.Objects);
    }

    [Fact]
    public async Task Grants_are_stored_while_public_and_apply_again_when_restricted()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var granted = c.H.AddCustomer("Granted");
        var other = c.H.AddCustomer("Other");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [granted.Id], null), CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Public", [granted.Id], null), CancellationToken.None);

        Assert.Contains(granted.Id, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.CustomerIds);
        Assert.NotNull(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [granted.Id], null), CancellationToken.None);
        Assert.NotNull(await c.Portal.GetProductAsync(granted.Id, product.Id, CancellationToken.None));
        Assert.Null(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
    }

}
