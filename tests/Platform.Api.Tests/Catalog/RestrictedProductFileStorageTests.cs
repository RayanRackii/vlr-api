using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Catalog.Controllers;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Core.Domain.Entities;
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

    private static async Task<Ctx> CreateAsync(TimeProvider? clock = null)
    {
        var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options, clock);
        var groups = new CustomerGroupService(h.Db, h.TenantProvider);
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

    [Theory]
    [InlineData("Public", CatalogFileVisibility.CustomerVisible)]
    [InlineData("Restricted", CatalogFileVisibility.CustomerVisible)]
    [InlineData("Public", CatalogFileVisibility.InternalB2B)]
    [InlineData("Restricted", CatalogFileVisibility.InternalB2B)]
    public async Task Visibility_change_with_private_files_is_allowed(string initial, CatalogFileVisibility fileVisibility)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new(initial, null, null), CancellationToken.None);
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        using var stream = new MemoryStream(fileVisibility == CatalogFileVisibility.InternalB2B ? "%PDF-1.4 dummy"u8.ToArray() : png);
        await c.Products.AddFileAsync(product.Id, fileVisibility == CatalogFileVisibility.InternalB2B ? "doc.pdf" : "photo.png", fileVisibility == CatalogFileVisibility.InternalB2B ? "application/pdf" : "image/png", stream, fileVisibility, CancellationToken.None);
        var original = Assert.Single(c.Storage.Uploads);

        var target = initial == "Public" ? "Restricted" : "Public";
        await c.Groups.SetProductAccessAsync(product.Id, new(target, null, null), CancellationToken.None);

        Assert.Equal(target, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        var read = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Single(read!.Files);
        Assert.True(read.HasFiles);
        Assert.Contains(original, c.Storage.Objects);
        Assert.Empty(c.Storage.Deletes);
    }

    [Theory]
    [InlineData("Public", "Restricted")]
    [InlineData("Restricted", "Public")]
    public async Task Visibility_change_without_files_is_allowed(string initial, string target)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new(initial, null, null), CancellationToken.None);
        var result = await c.Groups.SetProductAccessAsync(product.Id, new(target, null, null), CancellationToken.None);
        Assert.Equal(target, result!.Visibility);
        Assert.False((await c.Products.GetByIdAsync(product.Id, CancellationToken.None))!.HasFiles);
        Assert.Equal(target, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
    }

    [Fact]
    public async Task Restricted_upload_uses_private_bucket_and_public_grants_edit_with_files_is_allowed()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Granted");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await AddPhotoAsync(c, product.Id);
        await c.Groups.SetProductAccessAsync(product.Id, new("Public", [customer.Id], null), CancellationToken.None);
        Assert.Contains(customer.Id, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.CustomerIds);

        var restricted = await c.Products.CreateAsync(new() { Name = "Private", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(restricted.Id, new("Restricted", null, null), CancellationToken.None);
        var file = await AddPhotoAsync(c, restricted.Id);
        Assert.Null(file.Url);
        Assert.Equal(Opts.PrivateBucket, c.Storage.Uploads.Last().Bucket);
    }

    [Fact]
    public async Task Grants_only_save_failure_throws_and_preserves_stored_grants()
    {
        var provider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var failure = new FailSave();
        await using var db = InMemoryAppDb.Create(provider, interceptors: [failure]);
        var product = new CatalogProduct { TenantId = provider.TenantId!.Value, Name = "Item" };
        var group = new CustomerGroup { TenantId = provider.TenantId.Value, Name = "VIP" };
        db.CatalogProducts.Add(product);
        db.CustomerGroups.Add(group);
        await db.SaveChangesAsync();
        var groups = new CustomerGroupService(db, provider);
        failure.Enabled = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.SetProductAccessAsync(
            product.Id, new("Public", null, [group.Id]), CancellationToken.None));

        Assert.Empty((await groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.GroupIds);
        Assert.Empty(await db.CatalogAccessAudit.ToListAsync());
        var response = await new CustomerGroupsController(groups).SetAccess(
            product.Id, new("Public", null, [group.Id]), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(response.Result);
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) throw new InvalidOperationException("Save failed.");
            return ValueTask.FromResult(result);
        }
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
        await AddPhotoAsync(c, product.Id);
        await c.Groups.SetProductAccessAsync(product.Id, new("Public", [granted.Id], null), CancellationToken.None);

        Assert.Contains(granted.Id, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.CustomerIds);
        Assert.NotNull(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [granted.Id], null), CancellationToken.None);
        Assert.NotNull(await c.Portal.GetProductAsync(granted.Id, product.Id, CancellationToken.None));
        var signedBefore = c.Storage.Signed.Count;
        Assert.Null(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
        Assert.Equal(signedBefore, c.Storage.Signed.Count);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("upload")]
    [InlineData("ready")]
    [InlineData("ambiguous")]
    public async Task Failed_upload_retains_integrity_row_without_blocking_visibility(string step)
    {
        var provider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        var failure = new UploadSaveFailure();
        await using var db = InMemoryAppDb.Create(provider, interceptors: [failure]);
        var storage = new RecordingStorageProvider { FailUpload = step == "upload" };
        var products = new CatalogProductService(db, provider, storage, Options.Create(Opts));
        var groups = new CustomerGroupService(db, provider);
        var product = await products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        failure.Arm(step == "upload" ? 0 : step == "pending" ? 1 : 2, step == "ambiguous");
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        using var stream = new MemoryStream(png);

        await Assert.ThrowsAsync<InvalidOperationException>(() => products.AddFileAsync(product.Id,
            "photo.png", "image/png", stream, CatalogFileVisibility.CustomerVisible, CancellationToken.None));

        db.ChangeTracker.Clear();
        var row = Assert.Single(await db.CatalogProductFiles.ToListAsync());
        Assert.Equal(step == "ambiguous" ? CatalogFileStatus.Ready : CatalogFileStatus.Pending, row.Status);
        Assert.NotEqual("pending", row.StorageKey);
        Assert.True((await products.GetByIdAsync(product.Id, CancellationToken.None))!.HasFiles);
        await groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        Assert.Empty(storage.Deletes);
        if (step == "pending") Assert.Empty(storage.Uploads);
        else Assert.Contains((Opts.PrivateBucket, row.StorageKey), storage.Objects);
    }

    [Theory]
    [InlineData(false, CatalogFileStatus.Pending)]
    [InlineData(true, CatalogFileStatus.Pending)]
    [InlineData(false, CatalogFileStatus.Failed)]
    [InlineData(true, CatalogFileStatus.Failed)]
    public async Task Delete_incomplete_file_is_idempotent_and_then_allows_visibility_change(bool objectExists, CatalogFileStatus status)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var row = new CatalogProductFile { TenantId = c.H.TenantProvider.TenantId!.Value, ProductId = product.Id,
            StorageKey = "incomplete.png", FileName = "photo.png", MimeType = "image/png", SizeBytes = 16,
            Visibility = CatalogFileVisibility.CustomerVisible, Status = status };
        c.H.Db.CatalogProductFiles.Add(row);
        await c.H.Db.SaveChangesAsync();
        if (objectExists) c.Storage.Objects.Add((Opts.PrivateBucket, row.StorageKey));

        var clock = new TestTimeProvider(row.CreatedAt);
        var products = new CatalogProductService(c.H.Db, c.H.TenantProvider, c.Storage, Options.Create(Opts), clock);
        await products.DeleteFileAsync(product.Id, row.Id, CancellationToken.None);

        Assert.Empty(await c.H.Db.CatalogProductFiles.ToListAsync());
        Assert.DoesNotContain((Opts.PrivateBucket, row.StorageKey), c.Storage.Objects);
        Assert.Single(c.Storage.Deletes);
        Assert.False((await c.Products.GetByIdAsync(product.Id, CancellationToken.None))!.HasFiles);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Restricted")]
    public async Task Portal_excludes_incomplete_files_but_staff_can_delete_them(string visibility)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Granted");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Products.ActivateAsync(product.Id, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new(visibility, [customer.Id], null), CancellationToken.None);
        var ready = await AddPhotoAsync(c, product.Id);
        foreach (var status in new[] { CatalogFileStatus.Pending, CatalogFileStatus.Failed })
            c.H.Db.CatalogProductFiles.Add(new CatalogProductFile {
                TenantId = c.H.TenantProvider.TenantId!.Value, ProductId = product.Id,
                StorageKey = status + ".png", FileName = status + ".png", MimeType = "image/png", SizeBytes = 16,
                Visibility = CatalogFileVisibility.CustomerVisible, Status = status });
        await c.H.Db.SaveChangesAsync();

        var portal = await c.Portal.GetProductAsync(customer.Id, product.Id, CancellationToken.None);
        Assert.Equal(ready.Id, Assert.Single(portal!.Files).Id);
        var staff = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.True(staff!.HasFiles);
        Assert.Equal(3, staff.Files.Count);
        Assert.All(staff.Files.Where(f => f.Id != ready.Id), f => Assert.Null(f.Url));
    }

    [Fact]
    public async Task Late_private_completion_after_immediate_removal_and_restriction_cannot_be_served()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Other");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        await c.Products.ActivateAsync(product.Id, CancellationToken.None);
        c.Storage.BeforeUploadAsync = ct => Task.FromException(new TimeoutException("Storage POST timed out."));
        await Assert.ThrowsAsync<TimeoutException>(() => AddPhotoAsync(c, product.Id));
        var row = await c.H.Db.CatalogProductFiles.SingleAsync();
        var controller = new CatalogProductsController(c.Products, null!);
        Assert.IsType<NoContentResult>(await controller.DeleteFile(product.Id, row.Id, CancellationToken.None));
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
        c.Storage.Objects.Add((Opts.PrivateBucket, row.StorageKey));
        Assert.Empty(await c.H.Db.CatalogProductFiles.ToListAsync());
        Assert.Equal(Opts.PrivateBucket, Assert.Single(c.Storage.Deletes).Bucket);
        Assert.Null(await c.Portal.GetProductAsync(customer.Id, product.Id, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => c.Products.GetFileUrlAsync(product.Id, row.Id, CancellationToken.None));
        Assert.Empty(c.Storage.Signed);
    }

    [Fact]
    public async Task Public_upload_uses_private_bucket()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var file = await AddPhotoAsync(c, product.Id);
        Assert.Equal(Opts.PrivateBucket, Assert.Single(c.Storage.Uploads).Bucket);
        Assert.False((await c.H.Db.CatalogProductFiles.SingleAsync()).IsLegacyPublic);
        Assert.Null(file.Url);
    }

    [Fact]
    public async Task Public_portal_file_uses_signed_private_url()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var file = await AddPhotoAsync(c, product.Id);
        var portal = await c.Portal.GetProductAsync(null, product.Id, CancellationToken.None);
        Assert.StartsWith($"https://storage.test/sign/{Opts.PrivateBucket}/", Assert.Single(portal!.Files).Url);
        Assert.Equal(Opts.PrivateBucket, Assert.Single(c.Storage.Signed).Bucket);
        var admin = await c.Products.GetFileUrlAsync(product.Id, file.Id, CancellationToken.None);
        Assert.False(admin.IsPublic);
        Assert.All(c.Storage.SignedTtls, ttl => Assert.Equal(TimeSpan.FromSeconds(120), ttl));
        Assert.Equal(2, c.Storage.SignedTtls.Count);
    }

    private static async Task<CatalogProductFile> AddLegacyPhotoAsync(Ctx c, Guid productId)
    {
        var row = new CatalogProductFile { TenantId = c.H.TenantProvider.TenantId!.Value, ProductId = productId,
            StorageKey = "legacy.png", FileName = "legacy.png", MimeType = "image/png", SizeBytes = 16,
            Visibility = CatalogFileVisibility.CustomerVisible, IsLegacyPublic = true };
        c.H.Db.CatalogProductFiles.Add(row);
        await c.H.Db.SaveChangesAsync();
        c.Storage.Objects.Add((Opts.PublicBucket, row.StorageKey));
        return row;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_public_file_only_served_while_product_is_public(bool restricted)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var customer = c.H.AddCustomer("Granted");
        await c.H.Db.SaveChangesAsync();
        var product = await c.Products.CreateAsync(new() { Name = "Legacy", Price = 1m }, CancellationToken.None);
        if (restricted) await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [customer.Id], null), CancellationToken.None);
        var row = await AddLegacyPhotoAsync(c, product.Id);
        var portal = await c.Portal.GetProductAsync(customer.Id, product.Id, CancellationToken.None);
        if (restricted)
        {
            Assert.Empty(portal!.Files);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => c.Products.GetFileUrlAsync(product.Id, row.Id, CancellationToken.None));
        }
        else
        {
            Assert.Equal($"https://storage.test/{Opts.PublicBucket}/legacy.png", Assert.Single(portal!.Files).Url);
            Assert.True((await c.Products.GetFileUrlAsync(product.Id, row.Id, CancellationToken.None)).IsPublic);
        }
        Assert.Empty(c.Storage.Signed);
    }

    [Fact]
    public async Task Legacy_public_files_block_restriction_with_409_until_removed()
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Legacy", Price = 1m }, CancellationToken.None);
        var row = await AddLegacyPhotoAsync(c, product.Id);
        var response = await new CustomerGroupsController(c.Groups).SetAccess(
            product.Id, new("Restricted", null, null), CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.Equal(409, conflict.StatusCode);
        var body = System.Text.Json.JsonSerializer.SerializeToElement(conflict.Value);
        Assert.Equal("catalog.visibility.filesPresent", body.GetProperty("code").GetString());
        Assert.Contains("re-upload", body.GetProperty("error").GetString());
        Assert.Equal("Public", (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        Assert.Empty(c.Storage.Deletes);
        await c.Products.DeleteFileAsync(product.Id, row.Id, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", null, null), CancellationToken.None);
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Restricted")]
    public async Task Legacy_delete_uses_recorded_public_bucket(string visibility)
    {
        var c = await CreateAsync();
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Legacy", Price = 1m }, CancellationToken.None);
        await c.Groups.SetProductAccessAsync(product.Id, new(visibility, null, null), CancellationToken.None);
        var row = await AddLegacyPhotoAsync(c, product.Id);
        await c.Products.DeleteFileAsync(product.Id, row.Id, CancellationToken.None);
        Assert.Equal((Opts.PublicBucket, row.StorageKey), Assert.Single(c.Storage.Deletes));
        Assert.Empty(c.Storage.Objects);
        Assert.Empty(await c.H.Db.CatalogProductFiles.ToListAsync());
    }

    [Fact]
    public async Task Ready_delete_is_immediate_and_upload_timestamp_uses_injected_clock()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
        var c = await CreateAsync(clock);
        await using var _ = c.H;
        var product = await c.Products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var file = await AddPhotoAsync(c, product.Id);
        var row = await c.H.Db.CatalogProductFiles.SingleAsync();
        Assert.Equal(clock.GetUtcNow(), row.CreatedAt);
        Assert.Equal(CatalogFileStatus.Ready, row.Status);

        await c.Products.DeleteFileAsync(product.Id, file.Id, CancellationToken.None);

        Assert.Empty(await c.H.Db.CatalogProductFiles.ToListAsync());
        Assert.Empty(c.Storage.Objects);
        Assert.Single(c.Storage.Deletes);
    }

    [Fact]
    public async Task Delayed_pending_commit_cannot_start_a_fresh_storage_request_window()
    {
        var clock = new TestTimeProvider(DateTimeOffset.Parse("2026-11-04T12:00:00Z"));
        var provider = new FakeTenantProvider { TenantId = Guid.NewGuid() };
        await using var db = InMemoryAppDb.Create(provider, interceptors: [new DelayedPendingSave(clock)]);
        var storage = new RecordingStorageProvider();
        var products = new CatalogProductService(db, provider, storage, Options.Create(Opts), clock);
        var product = await products.CreateAsync(new() { Name = "Item", Price = 1m }, CancellationToken.None);
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        using var stream = new MemoryStream(png);

        await Assert.ThrowsAsync<TimeoutException>(() => products.AddFileAsync(product.Id,
            "photo.png", "image/png", stream, CatalogFileVisibility.CustomerVisible, CancellationToken.None));

        Assert.Empty(storage.Uploads);
        Assert.Equal(CatalogFileStatus.Pending, (await db.CatalogProductFiles.SingleAsync()).Status);
        Assert.True((await products.GetByIdAsync(product.Id, CancellationToken.None))!.HasFiles);
    }

    private sealed class DelayedPendingSave(TestTimeProvider clock) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<CatalogProductFile>()
                .Any(e => e.Entity.Status == CatalogFileStatus.Pending))
                clock.Advance(TimeSpan.FromSeconds(101));
            return ValueTask.FromResult(result);
        }
    }

}
