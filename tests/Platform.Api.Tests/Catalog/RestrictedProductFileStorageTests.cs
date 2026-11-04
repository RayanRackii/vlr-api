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

    private static async Task<Ctx> CreateAsync()
    {
        var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var options = Options.Create(Opts);
        var products = new CatalogProductService(h.Db, h.TenantProvider, storage, options);
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
    public async Task Visibility_change_with_any_file_is_rejected(string initial, CatalogFileVisibility fileVisibility)
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

        await Assert.ThrowsAsync<CatalogVisibilityFilesPresentException>(() => c.Groups.SetProductAccessAsync(
            product.Id, new(initial == "Public" ? "Restricted" : "Public", null, null), CancellationToken.None));

        Assert.Equal(initial, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.Visibility);
        var read = await c.Products.GetByIdAsync(product.Id, CancellationToken.None);
        Assert.Single(read!.Files);
        Assert.True(read.HasFiles);
        Assert.Contains(original, c.Storage.Objects);
        Assert.Empty(c.Storage.Copies);
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
        Assert.Empty(c.Storage.Copies);
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
        Assert.Empty(c.Storage.Copies);
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
        await c.Groups.SetProductAccessAsync(product.Id, new("Public", [granted.Id], null), CancellationToken.None);

        Assert.Contains(granted.Id, (await c.Groups.GetProductAccessAsync(product.Id, CancellationToken.None))!.CustomerIds);
        Assert.NotNull(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
        await c.Groups.SetProductAccessAsync(product.Id, new("Restricted", [granted.Id], null), CancellationToken.None);
        Assert.NotNull(await c.Portal.GetProductAsync(granted.Id, product.Id, CancellationToken.None));
        Assert.Null(await c.Portal.GetProductAsync(other.Id, product.Id, CancellationToken.None));
    }

}
