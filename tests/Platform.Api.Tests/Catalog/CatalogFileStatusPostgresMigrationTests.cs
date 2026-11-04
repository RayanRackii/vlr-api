using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Platform.Api.Tests.Fakes;
using Platform.Api.Tests.Infrastructure;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Tests.Catalog;

// Own fixture: schema rollback must not affect other test classes.
public sealed class CatalogFileStatusPostgresMigrationTests(PostgresContainerFixture postgres)
    : IClassFixture<PostgresContainerFixture>
{
    [DockerFact]
    public async Task Existing_customer_visible_files_are_legacy_internal_files_are_private_and_empty_up_down_up_is_safe()
    {
        var factory = Assert.IsType<PostgresAppDbFactory>(postgres.Factory);
        var provider = new FakeTenantProvider();
        await using var db = factory.Create(provider);
        var migrator = db.GetService<IMigrator>();
        const string before = "20261103230116_AddCustomerGroupsAndProductVisibility";
        const string target = "20261104010000_AddCatalogProductFileStatus";
        await migrator.MigrateAsync(before);
        var tenant = new Tenant("Migration test", Guid.NewGuid().ToString("N")[..20]);
        provider.TenantId = tenant.Id;
        db.Tenants.Add(tenant);
        var product = new CatalogProduct { TenantId = tenant.Id, Name = "Migration product" };
        db.CatalogProducts.Add(product);
        await db.SaveChangesAsync();
        foreach (var visibility in new[] { "CustomerVisible", "InternalB2B" })
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO catalog.catalog_product_files
                    (id, tenant_id, product_id, storage_key, file_name, mime_type, size_bytes, visibility, created_at)
                VALUES ({Guid.NewGuid()}, {tenant.Id}, {product.Id}, {visibility}, {visibility}, 'image/png', 16, {visibility}, now())
                """);
        }

        await migrator.MigrateAsync(target);
        var files = await db.CatalogProductFiles.AsNoTracking().ToListAsync();
        Assert.Equal(2, files.Count);
        Assert.True(Assert.Single(files, file => file.Visibility == CatalogFileVisibility.CustomerVisible).IsLegacyPublic);
        Assert.False(Assert.Single(files, file => file.Visibility == CatalogFileVisibility.InternalB2B).IsLegacyPublic);
        Assert.All(files, file => Assert.Equal(CatalogFileStatus.Ready, file.Status));

        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(before));
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM catalog.catalog_product_files WHERE product_id = {product.Id}");
        await migrator.MigrateAsync(before);
        await migrator.MigrateAsync(target);
        Assert.Empty(await db.CatalogProductFiles.ToListAsync());
    }
}
