using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.CompanyProfile;

public sealed class CompanyContactPriceUnitMigrationTests
{
    [Fact]
    public void Up_only_adds_nullable_columns_without_defaults_or_data_changes()
    {
        var operations = new AddCompanyContactPriceUnitAndReservationNotes().UpOperations;

        Assert.All(operations.OfType<AddColumnOperation>(), add =>
        {
            Assert.True(add.IsNullable);
            Assert.Null(add.DefaultValue);
        });
        Assert.DoesNotContain(operations, operation => operation is DropColumnOperation or AlterColumnOperation or DeleteDataOperation or UpdateDataOperation);

        // The only data statement: idempotent insert of the permission row and its grant.
        var sql = Assert.Single(operations.OfType<SqlOperation>());
        Assert.Contains("core.company.write", sql.Sql, StringComparison.Ordinal);
        Assert.Equal(2, sql.Sql.Split("ON CONFLICT", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("DELETE", sql.Sql, StringComparison.OrdinalIgnoreCase);

        var priceUnit = Assert.Single(operations.OfType<AddColumnOperation>(), op => op.Name == "price_unit");
        Assert.Equal("catalog", priceUnit.Schema);
        Assert.Equal("catalog_products", priceUnit.Table);

        var notes = Assert.Single(operations.OfType<AddColumnOperation>(), op => op.Name == "notes");
        Assert.Equal("rentals", notes.Schema);
        Assert.Equal("reservations", notes.Table);
        Assert.Equal(1000, notes.MaxLength);

        Assert.Equal(
            [
                "address_city", "address_complement", "address_district", "address_number",
                "address_postal_code", "address_state", "address_street", "contact_email", "contact_phone",
            ],
            operations.OfType<AddColumnOperation>()
                .Where(op => op.Table == "tenants")
                .Select(op => op.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(11, operations.OfType<AddColumnOperation>().Count());
        Assert.Equal(12, operations.Count);
    }

    [Fact]
    public void Down_drops_exactly_the_added_columns()
    {
        var migration = new AddCompanyContactPriceUnitAndReservationNotes();

        var added = migration.UpOperations.OfType<AddColumnOperation>()
            .Select(op => (op.Schema, op.Table, op.Name))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();
        var dropped = migration.DownOperations.OfType<DropColumnOperation>()
            .Select(op => (op.Schema, op.Table, op.Name))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(migration.DownOperations.Count - 1, dropped.Length);
        Assert.Single(migration.DownOperations.OfType<SqlOperation>());
        Assert.Equal(added, dropped);
    }
}
