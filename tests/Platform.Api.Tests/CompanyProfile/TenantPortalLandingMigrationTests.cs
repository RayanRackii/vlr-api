using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Platform.Core.Infrastructure.Persistence.Migrations;

namespace Platform.Api.Tests.CompanyProfile;

public sealed class TenantPortalLandingMigrationTests
{
    private static readonly (string Name, int Length)[] Expected =
    [
        ("landing_headline", 120),
        ("landing_image_key", 300),
        ("landing_kicker", 60),
        ("landing_subtitle", 240),
    ];

    [Fact]
    public void Up_only_adds_four_nullable_tenant_columns_without_defaults_or_data_changes()
    {
        var operations = new AddTenantPortalLanding().UpOperations;

        Assert.Equal(4, operations.Count);
        var adds = operations.OfType<AddColumnOperation>().ToArray();
        Assert.Equal(4, adds.Length);
        Assert.All(adds, add =>
        {
            Assert.Equal("core", add.Schema);
            Assert.Equal("tenants", add.Table);
            Assert.True(add.IsNullable);
            Assert.Null(add.DefaultValue);
        });
        Assert.Equal(
            Expected,
            adds.Select(add => (add.Name, add.MaxLength!.Value)).OrderBy(x => x.Name, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Down_drops_exactly_the_added_columns()
    {
        var migration = new AddTenantPortalLanding();

        var dropped = migration.DownOperations.OfType<DropColumnOperation>()
            .Select(op => (op.Schema, op.Table, op.Name))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(migration.DownOperations.Count, dropped.Length);
        Assert.Equal(
            Expected.Select(e => ("core", "tenants", e.Name)).ToArray(),
            dropped);
    }
}
