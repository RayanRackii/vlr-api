using Platform.Core.Domain.Constants;

namespace Platform.Api.Tests.Catalog;

public sealed class CustomerDocumentBackfillMigrationTests
{
    [Fact]
    public void Migration_sql_backfills_document_from_cpf()
    {
        var migrationsDir = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "Core",
                "Platform.Core.Infrastructure",
                "Persistence",
                "Migrations"));

        var files = Directory.GetFiles(migrationsDir, "*AddCatalogOrdersAndCustomerDocument*.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(files.Length == 1, $"Expected one catalog migration in {migrationsDir}");
        var sql = File.ReadAllText(files[0]);
        var up = sql.Split("protected override void Down", 2)[0];
        Assert.Contains("document = cpf", up, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DropColumn", up, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("name: \"cpf\"", up, StringComparison.OrdinalIgnoreCase);
    }
}
