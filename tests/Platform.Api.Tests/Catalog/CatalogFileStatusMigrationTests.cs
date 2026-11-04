using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.Catalog;

public sealed class CatalogFileStatusMigrationTests
{
    [Fact]
    public void Up_marks_only_existing_customer_visible_files_legacy_in_the_same_transaction()
    {
        var operations = new AddCatalogProductFileStatus().UpOperations;
        var backfill = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Equal("UPDATE catalog.catalog_product_files SET is_legacy_public = true WHERE visibility = 'CustomerVisible';",
            backfill.Sql.Trim());
        Assert.False(backfill.SuppressTransaction);
        Assert.Equal(2, operations.TakeWhile(operation => operation != backfill).OfType<AddColumnOperation>().Count());
        Assert.Equal(false, Assert.Single(operations.OfType<AddColumnOperation>(), column => column.Name == "is_legacy_public").DefaultValue);
        Assert.Equal("Ready", Assert.Single(operations.OfType<AddColumnOperation>(), column => column.Name == "status").DefaultValue);
    }
}
