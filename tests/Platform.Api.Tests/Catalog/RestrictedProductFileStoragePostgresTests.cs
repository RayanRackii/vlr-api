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

    [DockerFact]
    public async Task Opening_timeout_after_copy_is_verified_and_commits_public()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using var db = factory.Create(provider);
        var groups = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));
        await groups.SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None);
        var products = new CatalogProductService(db, provider, storage, Options.Create(Opts));
        var file = await AddPhotoAsync(products, productId, CancellationToken.None);
        storage.FailAfterCopy = true;

        await groups.SetProductAccessAsync(productId, new("Public", null, null), CancellationToken.None);

        var copy = Assert.Single(storage.Copies);
        Assert.Contains((Opts.PublicBucket, copy.ToKey), storage.Objects);
        Assert.DoesNotContain((Opts.PrivateBucket, copy.FromKey), storage.Objects);
        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Public,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
        Assert.NotNull(await verify.CatalogProductFiles.SingleOrDefaultAsync(f => f.Id == file.Id));
    }

    [DockerFact]
    public async Task Opening_commit_failure_rolls_back_and_removes_public_destination()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using (var setup = factory.Create(provider))
        {
            var groups = new CustomerGroupService(setup, provider, null, storage, Options.Create(Opts));
            await groups.SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None);
            var products = new CatalogProductService(setup, provider, storage, Options.Create(Opts));
            await AddPhotoAsync(products, productId, CancellationToken.None);
        }
        var failure = new TimeoutException("Commit failed before completion.");
        await using var db = factory.Create(provider, new FailCommit(failure));
        var access = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            access.SetProductAccessAsync(productId, new("Public", null, null), CancellationToken.None));

        Assert.Same(failure, thrown);
        var copy = Assert.Single(storage.Copies);
        Assert.DoesNotContain((Opts.PublicBucket, copy.ToKey), storage.Objects);
        Assert.Contains((Opts.PrivateBucket, copy.FromKey), storage.Objects);
        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Restricted,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
    }

    [DockerFact]
    public async Task Partial_public_delete_failure_commits_public_revert_then_restores_every_file()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using var db = factory.Create(provider);
        var products = new CatalogProductService(db, provider, storage, Options.Create(Opts));
        await AddPhotoAsync(products, productId, CancellationToken.None);
        await AddPhotoAsync(products, productId, CancellationToken.None);
        storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
        storage.FailRequiredDeleteAfter = 1;
        storage.BeforeCopy = (bucket, key) =>
        {
            if (bucket != Opts.PublicBucket) return;
            using var committed = factory.Create(provider);
            Assert.Equal(CatalogProductVisibility.Public,
                committed.CatalogProducts.AsNoTracking().Single(p => p.Id == productId).Visibility);
        };
        var groups = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            groups.SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None));

        Assert.Contains("reverted to Public", error.Message);
        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Public,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
        Assert.Equal(3, storage.Copies.Count); // Two private copies, one public restoration.
        foreach (var upload in storage.Uploads)
        {
            Assert.Contains((Opts.PublicBucket, upload.Key), storage.Objects);
            Assert.Contains((Opts.PrivateBucket, upload.Key), storage.Objects);
        }
        Assert.DoesNotContain(storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
    }

    [DockerFact]
    public async Task Restricting_commit_failure_keeps_public_files_and_never_deletes_either_copy()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using (var setup = factory.Create(provider))
            await AddPhotoAsync(new CatalogProductService(setup, provider, storage, Options.Create(Opts)), productId, CancellationToken.None);
        await using var db = factory.Create(provider, new FailCommit(new TimeoutException("Commit failed.")));
        var groups = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));

        await Assert.ThrowsAsync<TimeoutException>(() =>
            groups.SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None));

        await using var verify = factory.Create(provider);
        Assert.Equal(CatalogProductVisibility.Public,
            (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility);
        Assert.Empty(storage.Deletes);
        var copy = Assert.Single(storage.Copies);
        Assert.Contains((Opts.PublicBucket, copy.FromKey), storage.Objects);
        Assert.Contains((Opts.PrivateBucket, copy.ToKey), storage.Objects);
    }

    [DockerFact]
    public async Task Restricting_commit_timeout_after_success_reads_committed_state_and_removes_public_copy()
        => await AssertCommittedTimeoutAsync("Restricted");

    [DockerFact]
    public async Task Opening_commit_timeout_after_success_keeps_public_copy_and_removes_private_copy()
        => await AssertCommittedTimeoutAsync("Public");

    [DockerFact]
    public async Task Opening_commit_failure_and_partial_public_cleanup_recovers_public_and_retains_all_private_files()
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using (var setup = factory.Create(provider))
        {
            await new CustomerGroupService(setup, provider, null, storage, Options.Create(Opts))
                .SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None);
            var products = new CatalogProductService(setup, provider, storage, Options.Create(Opts));
            for (var i = 0; i < 3; i++) await AddPhotoAsync(products, productId, CancellationToken.None);
        }
        storage.FailRequiredDeleteInBucket = Opts.PublicBucket;
        storage.FailRequiredDeleteAfter = 1;
        var failure = new TimeoutException("Opening commit failed before completion.");
        await using var db = factory.Create(provider, new FailCommit(failure, once: true));
        var groups = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));

        var thrown = await Assert.ThrowsAsync<TimeoutException>(() =>
            groups.SetProductAccessAsync(productId, new("Public", null, null), CancellationToken.None));

        Assert.Same(failure, thrown);
        await using var verify = factory.Create(provider);
        var access = new CustomerGroupService(verify, provider, null, storage, Options.Create(Opts));
        Assert.Equal("Public", (await access.GetProductAccessAsync(productId, CancellationToken.None))!.Visibility);
        foreach (var upload in storage.Uploads)
        {
            Assert.Contains((Opts.PrivateBucket, upload.Key), storage.Objects);
            Assert.Contains((Opts.PublicBucket, upload.Key), storage.Objects);
        }
        Assert.DoesNotContain(storage.Deletes, d => d.Bucket == Opts.PrivateBucket);
        Assert.Contains(storage.Deletes, d => d.Bucket == Opts.PublicBucket);
    }

    private async Task AssertCommittedTimeoutAsync(string target)
    {
        var factory = RequireFactory();
        var provider = new FakeTenantProvider();
        var storage = new RecordingStorageProvider();
        var productId = await SeedProductAsync(factory, provider, storage);
        await using (var setup = factory.Create(provider))
        {
            if (target == "Public")
                await new CustomerGroupService(setup, provider, null, storage, Options.Create(Opts))
                    .SetProductAccessAsync(productId, new("Restricted", null, null), CancellationToken.None);
            await AddPhotoAsync(new CatalogProductService(setup, provider, storage, Options.Create(Opts)), productId, CancellationToken.None);
        }
        await using var db = factory.Create(provider, new TimeoutAfterCommit());
        var groups = new CustomerGroupService(db, provider, null, storage, Options.Create(Opts));

        await groups.SetProductAccessAsync(productId, new(target, null, null), CancellationToken.None);

        await using var verify = factory.Create(provider);
        Assert.Equal(target, (await verify.CatalogProducts.SingleAsync(p => p.Id == productId)).Visibility.ToString());
        var copy = Assert.Single(storage.Copies);
        Assert.Contains((copy.ToBucket, copy.ToKey), storage.Objects);
        Assert.DoesNotContain((copy.FromBucket, copy.FromKey), storage.Objects);
    }

    private sealed class TimeoutAfterCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
            => throw new TimeoutException("Commit timed out after success.");
    }

    private sealed class FailCommit(Exception failure, bool once = false) : DbTransactionInterceptor
    {
        private bool failed;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!once || !failed)
            {
                failed = true;
                throw failure;
            }
            return ValueTask.FromResult(result);
        }
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
