using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Tests.Catalog;

/// <summary>
/// Real-PostgreSQL coverage for the product-row lock that the InMemory suite cannot exercise:
/// uploads and visibility edits serialize on the same product row; failed saves surface as errors.
/// </summary>
public sealed class RestrictedProductFileStoragePostgresTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    private static readonly StorageOptions Opts = new();

    [DockerFact]
    public async Task Upload_blocks_on_restriction_lock_then_lands_in_private_bucket()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pause = new PauseSave();
        var observed = new ObserveProductLock();
        await using var toggleDb = factory.Create(provider, pause);
        await using var uploadDb = factory.Create(provider, observed);
        // Deliberately retain a stale tracked Public product before the restriction commits.
        await uploadDb.CatalogProducts.FirstAsync(p => p.Id == productId, timeout.Token);
        var uploads = new CatalogProductService(uploadDb, provider, storage, Options.Create(Opts));
        var groups = new CustomerGroupService(toggleDb, provider);
        var toggle = groups.SetProductAccessAsync(productId, new("Restricted", null, null), timeout.Token);
        await pause.Entered.Task.WaitAsync(timeout.Token);
        var upload = AddPhotoAsync(uploads, productId, timeout.Token);
        try
        {
            await observed.Attempted.Task.WaitAsync(timeout.Token);
            Assert.False(upload.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        await toggle;
        var file = await upload;
        Assert.Null(file.Url);
        Assert.Equal(Opts.PrivateBucket, Assert.Single(storage.Uploads).Bucket);
        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Restricted,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
        Assert.Single(await verify.CatalogProductFiles.Where(f => f.ProductId == productId).ToListAsync());
    }

    [DockerFact]
    public async Task Restriction_waits_for_upload_then_rejects_visibility_change()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pause = new PauseSave();
        var observed = new ObserveProductLock();
        await using var uploadDb = factory.Create(provider, pause);
        await using var toggleDb = factory.Create(provider, observed);
        var uploads = new CatalogProductService(uploadDb, provider, storage, Options.Create(Opts));
        var groups = new CustomerGroupService(toggleDb, provider);
        var upload = AddPhotoAsync(uploads, productId, timeout.Token);
        await pause.Entered.Task.WaitAsync(timeout.Token);
        var toggle = groups.SetProductAccessAsync(productId, new("Restricted", null, null), timeout.Token);
        try
        {
            await observed.Attempted.Task.WaitAsync(timeout.Token);
            Assert.False(toggle.IsCompleted);
        }
        finally { pause.Release.TrySetResult(); }
        await upload;
        await Assert.ThrowsAsync<CatalogVisibilityFilesPresentException>(() => toggle);
        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Public,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
        Assert.Single(await verify.CatalogProductFiles.Where(f => f.ProductId == productId).ToListAsync());
        Assert.Equal(Opts.PublicBucket, Assert.Single(storage.Uploads).Bucket);
        Assert.Empty(storage.Copies);
        Assert.Empty(storage.Deletes);
    }

    [DockerFact]
    public async Task Any_file_blocks_both_visibility_directions_but_empty_products_can_toggle()
    {
        var factory = RequireFactory();
        foreach (var initial in new[] { "Public", "Restricted" })
        foreach (var fileVisibility in new[] { CatalogFileVisibility.CustomerVisible, CatalogFileVisibility.InternalB2B })
        {
            var provider = new FakeTenantProvider();
            var storage = new RecordingStorageProvider();
            var productId = await SeedProductAsync(factory, provider, storage);
            await using var db = factory.Create(provider);
            var products = new CatalogProductService(db, provider, storage, Options.Create(Opts));
            var groups = new CustomerGroupService(db, provider);
            // Exercise each direction without files first.
            await groups.SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None);
            await groups.SetProductAccessAsync(productId, new("Public", null, null), CancellationToken.None);
            await groups.SetProductAccessAsync(productId, new(initial, null, null), CancellationToken.None);
            var png = new byte[16];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
            using var stream = new MemoryStream(fileVisibility == CatalogFileVisibility.InternalB2B ? "%PDF-1.4 dummy"u8.ToArray() : png);
            await products.AddFileAsync(productId, fileVisibility == CatalogFileVisibility.InternalB2B ? "doc.pdf" : "photo.png", fileVisibility == CatalogFileVisibility.InternalB2B ? "application/pdf" : "image/png", stream, fileVisibility, CancellationToken.None);
            await Assert.ThrowsAsync<CatalogVisibilityFilesPresentException>(() => groups.SetProductAccessAsync(
                productId, new(initial == "Public" ? "Restricted" : "Public", null, null), CancellationToken.None));
            Assert.Equal(initial, (await groups.GetProductAccessAsync(productId, CancellationToken.None))!.Visibility);
            Assert.Empty(storage.Copies);
            Assert.Empty(storage.Deletes);
            if (initial == "Restricted") Assert.Equal(Opts.PrivateBucket, Assert.Single(storage.Uploads).Bucket);
        }
    }

    [DockerFact]
    public async Task Grants_only_save_failure_is_reported_and_preserves_existing_grants()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var productId = await SeedProductAsync(factory, provider, new RecordingStorageProvider());
        Guid groupId;
        await using (var seed = factory.Create(provider))
        {
            var group = new CustomerGroup { TenantId = provider.TenantId!.Value, Name = "VIP" };
            seed.CustomerGroups.Add(group);
            await seed.SaveChangesAsync();
            groupId = group.Id;
        }
        await using var db = factory.Create(provider, new FailSave());
        var groups = new CustomerGroupService(db, provider);
        await Assert.ThrowsAsync<InvalidOperationException>(() => groups.SetProductAccessAsync(
            productId, new("Public", null, [groupId]), CancellationToken.None));
        await using var verify = factory.Create(provider);
        Assert.Empty(await verify.CatalogProductAccess.Where(a => a.ProductId == productId).ToListAsync());
        Assert.Empty(await verify.CatalogAccessAudit.Where(a => a.EntityId == productId).ToListAsync());
    }

    private sealed class FailSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Save failed.");
    }

    private sealed class PauseSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }

    [DockerFact]
    public async Task Delete_keeps_file_row_when_required_storage_delete_fails()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);

        await using var db = factory.Create(provider);
        var products = new CatalogProductService(db, provider, storage, Options.Create(Opts));
        var file = await AddPhotoAsync(products, productId, CancellationToken.None);

        storage.FailRequiredDelete = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            products.DeleteFileAsync(productId, file.Id, CancellationToken.None));

        // The row delete committed only after a successful object delete; the throw rolled it back.
        await using var verify = factory.Create(provider);
        Assert.NotNull(await verify.CatalogProductFiles.FirstOrDefaultAsync(f => f.Id == file.Id));
    }

    private PostgresAppDbFactory RequireFactory() => Assert.IsType<PostgresAppDbFactory>(postgres.Factory);

    private static async Task<Guid> SeedProductAsync(
        PostgresAppDbFactory factory, FakeTenantProvider provider, RecordingStorageProvider storage)
    {
        var tenant = new Tenant("Catalog Club", Random.Shared.NextInt64(10_000_000_000_000, 99_999_999_999_999).ToString(), subdomain: $"cat-{Guid.NewGuid():N}"[..12]);
        provider.TenantId = tenant.Id;
        await using var seed = factory.Create(provider);
        seed.Tenants.Add(tenant);
        seed.TenantModules.Add(new TenantModule(tenant.Id, PlatformModules.Catalog, true));
        await seed.SaveChangesAsync();
        var products = new CatalogProductService(seed, provider, storage, Options.Create(Opts));
        var product = await products.CreateAsync(
            new CreateCatalogProductRequest { Name = "Item", Price = 1m }, CancellationToken.None);
        return product.Id;
    }

    private static async Task<CatalogProductFileDto> AddPhotoAsync(
        CatalogProductService products, Guid productId, CancellationToken ct)
    {
        var png = new byte[16];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);
        await using var stream = new MemoryStream(png);
        return await products.AddFileAsync(productId, "photo.png", "image/png", stream,
            CatalogFileVisibility.CustomerVisible, ct);
    }

    private sealed class ObserveProductLock : DbCommandInterceptor
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("catalog.catalog_products", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                Attempted.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
