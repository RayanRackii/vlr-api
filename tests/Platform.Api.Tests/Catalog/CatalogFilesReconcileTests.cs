using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Platform.Api.Modules.Catalog;
using Platform.Api.Modules.Catalog.Services;
using Platform.Api.Storage;
using Platform.Api.Tests.Fakes;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogFilesReconcileTests
{
    private static readonly StorageOptions Storage = new();

    private static CatalogProductFile AddFile(CatalogHarness h, RecordingStorageProvider storage,
        CatalogProductVisibility productVisibility = CatalogProductVisibility.Public,
        CatalogFileVisibility fileVisibility = CatalogFileVisibility.CustomerVisible,
        bool publicObject = true, bool legacy = false, bool privateObject = false)
    {
        var product = new CatalogProduct { TenantId = h.Tenant.Id, Name = "Item", Visibility = productVisibility };
        var file = new CatalogProductFile
        {
            TenantId = h.Tenant.Id, ProductId = product.Id, StorageKey = "pending",
            FileName = "image.png", MimeType = "image/png", SizeBytes = 16,
            Visibility = fileVisibility, IsLegacyPublic = legacy
        };
        file.StorageKey = CatalogFileRules.StorageKey(file.TenantId, file.ProductId, file.Id);
        h.Db.AddRange(product, file);
        if (publicObject) storage.Objects.Add((Storage.PublicBucket, file.StorageKey));
        if (privateObject) storage.Objects.Add((Storage.PrivateBucket, file.StorageKey));
        return file;
    }

    [Fact]
    public async Task Report_only_changes_neither_rows_nor_objects()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var legacy = AddFile(h, storage);
        var restricted = AddFile(h, storage, CatalogProductVisibility.Restricted, legacy: true);
        var mismatch = AddFile(h, storage, publicObject: false, legacy: true);
        await h.Db.SaveChangesAsync();
        var output = new StringWriter();
        await new CatalogFilesReconciler(h.Db, storage, Options.Create(Storage)).RunAsync(output);
        Assert.False(legacy.IsLegacyPublic);
        Assert.True(restricted.IsLegacyPublic);
        Assert.Equal(CatalogFileStatus.Ready, restricted.Status);
        Assert.True(mismatch.IsLegacyPublic);
        Assert.Equal(2, storage.Objects.Count);
        Assert.Empty(storage.Deletes);
        Assert.Contains("class=legacy-ok", output.ToString());
        Assert.Contains("class=restricted-public", output.ToString());
        Assert.Contains("class=flag-mismatch", output.ToString());
    }

    [Fact]
    public async Task Apply_reconciles_public_objects_and_flags()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var legacy = AddFile(h, storage);
        var restricted = AddFile(h, storage, CatalogProductVisibility.Restricted, legacy: true);
        var internalFile = AddFile(h, storage, fileVisibility: CatalogFileVisibility.InternalB2B, privateObject: true);
        var mismatch = AddFile(h, storage, publicObject: false, legacy: true);
        var orphan = CatalogFileRules.StorageKey(h.Tenant.Id, Guid.NewGuid(), Guid.NewGuid());
        var malformed = $"{h.Tenant.Id:N}/{Guid.NewGuid():N}/invalid";
        var unrelated = $"{h.Tenant.Id:N}/work-orders/{Guid.NewGuid():N}/tasks/x/image";
        storage.Objects.UnionWith([(Storage.PublicBucket, orphan), (Storage.PublicBucket, malformed), (Storage.PublicBucket, unrelated)]);
        await h.Db.SaveChangesAsync();
        var reconciler = new CatalogFilesReconciler(h.Db, storage, Options.Create(Storage));
        var output = new StringWriter();
        Assert.True(await reconciler.RunAsync(output, apply: true) > 0);
        Assert.True(legacy.IsLegacyPublic);
        Assert.False(restricted.IsLegacyPublic);
        Assert.Equal(CatalogFileStatus.Failed, restricted.Status);
        Assert.False(internalFile.IsLegacyPublic);
        Assert.Equal(CatalogFileStatus.Ready, internalFile.Status);
        Assert.False(mismatch.IsLegacyPublic);
        Assert.Equal(4, storage.Deletes.Count);
        Assert.Contains((Storage.PublicBucket, legacy.StorageKey), storage.Objects);
        Assert.Contains((Storage.PublicBucket, unrelated), storage.Objects);
        Assert.Contains((Storage.PrivateBucket, internalFile.StorageKey), storage.Objects);
        Assert.Contains("missing-private", output.ToString());
        Assert.Contains("class=internal-public", output.ToString());
        Assert.Contains("class=orphan count=2", output.ToString());
        Assert.False(h.Db.ChangeTracker.HasChanges());
        Assert.Equal(legacy.Id, (await h.Db.CatalogProductFiles.SingleAsync(f => f.IsLegacyPublic)).Id);
    }

    [Theory]
    [InlineData(CatalogProductVisibility.Restricted, CatalogFileVisibility.CustomerVisible)]
    [InlineData(CatalogProductVisibility.Public, CatalogFileVisibility.InternalB2B)]
    public async Task Unsafe_public_file_without_private_copy_is_failed_and_never_served(
        CatalogProductVisibility productVisibility, CatalogFileVisibility fileVisibility)
    {
        await using var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var file = AddFile(h, storage, productVisibility, fileVisibility);
        await h.Db.SaveChangesAsync();
        await new CatalogFilesReconciler(h.Db, storage, Options.Create(Storage)).RunAsync(new StringWriter(), apply: true);
        Assert.Equal(CatalogFileStatus.Failed, file.Status);
        Assert.False(file.IsLegacyPublic);
        Assert.DoesNotContain((Storage.PublicBucket, file.StorageKey), storage.Objects);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => h.Products.GetFileUrlAsync(file.ProductId, file.Id, default));
    }

    [Fact]
    public async Task Repeated_apply_does_not_write_again()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        AddFile(h, storage);
        await h.Db.SaveChangesAsync();
        var reconciler = new CatalogFilesReconciler(h.Db, storage, Options.Create(Storage));
        Assert.Equal(1, await reconciler.RunAsync(new StringWriter(), apply: true));
        Assert.Equal(0, await reconciler.RunAsync(new StringWriter(), apply: true));
        Assert.Empty(storage.Deletes);
        Assert.False(h.Db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task Storage_key_cannot_flag_a_row_belonging_to_another_tenant()
    {
        await using var h = await CatalogHarness.CreateAsync();
        var storage = new RecordingStorageProvider();
        var file = AddFile(h, storage);
        file.TenantId = Guid.NewGuid();
        await h.Db.SaveChangesAsync();
        await new CatalogFilesReconciler(h.Db, storage, Options.Create(Storage)).RunAsync(new StringWriter(), apply: true);
        Assert.False(file.IsLegacyPublic);
        Assert.Equal(CatalogFileStatus.Ready, file.Status);
        Assert.Empty(storage.Objects);
    }

}
