using Microsoft.Extensions.Options;
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

    private static async Task<Ctx> CreateAsync()
    {
        var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options);
        var groups = new CustomerGroupService(h.Db, h.TenantProvider, null, storage, options);
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
}
