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
/// an upload must wait on a concurrent restriction's FOR UPDATE lock, and a failed required
/// storage delete must roll back so the file row survives.
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

        // Holder locks the product row and switches it to Restricted, keeping the tx open so the
        // concurrent upload below cannot read the product visibility until the holder commits.
        await using var holder = factory.Create(provider);
        await using var holderTx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM catalog.catalog_products WHERE tenant_id = {provider.TenantId} AND id = {productId} FOR UPDATE");
        var locked = await holder.CatalogProducts.FirstAsync(p => p.Id == productId);
        locked.Visibility = CatalogProductVisibility.Restricted;
        await holder.SaveChangesAsync();

        var observed = new ObserveProductLock();
        await using var uploadDb = factory.Create(provider, observed);
        var uploads = new CatalogProductService(uploadDb, provider, storage, Options.Create(Opts));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upload = AddPhotoAsync(uploads, productId, timeout.Token);

        await observed.Attempted.Task.WaitAsync(timeout.Token);
        Assert.False(upload.IsCompleted);

        await holderTx.CommitAsync(timeout.Token);

        var file = await upload;
        // Visibility was read AFTER the lock was released as Restricted, so the file is private.
        Assert.Null(file.Url);
        Assert.Equal(Opts.PrivateBucket, Assert.Single(storage.Uploads).Bucket);
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
