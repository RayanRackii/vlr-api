using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Core.Infrastructure.MigrationOps;

public static class CatalogPreflightDiagnostics
{
    public static async Task<CatalogPreflightDiagnosticCounts> CollectAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var catalogActiveTenants = await dbContext.TenantModules
            .IgnoreQueryFilters()
            .Where(module => module.IsActive && module.ModuleName.ToLower() == PlatformModules.Catalog)
            .Select(module => module.TenantId)
            .Distinct()
            .CountAsync(cancellationToken);

        if (!dbContext.Database.IsRelational())
        {
            var customers = await dbContext.Customers
                .IgnoreQueryFilters()
                .Select(customer => new CustomerCpfRow(customer.TenantId, customer.Cpf, customer.Document))
                .ToListAsync(cancellationToken);

            return Aggregate(
                customers.Select(row => (row.TenantId, row.Cpf)).ToList(),
                catalogActiveTenants,
                documentColumnPresent: true,
                documentPopulated: customers.Count(row => !string.IsNullOrWhiteSpace(row.Document)),
                documentConflicts: customers.Count(row =>
                    !string.IsNullOrWhiteSpace(row.Document)
                    && !string.IsNullOrWhiteSpace(row.Cpf)
                    && !string.Equals(row.Document, row.Cpf, StringComparison.Ordinal)));
        }

        await dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = dbContext.Database.GetDbConnection();
            var cpfRows = await ReadCpfRowsAsync(connection, cancellationToken);
            var documentPresent = await DocumentColumnExistsAsync(connection, cancellationToken);
            var documentPopulated = 0;
            var documentConflicts = 0;
            if (documentPresent)
            {
                (documentPopulated, documentConflicts) = await ReadDocumentStatsAsync(connection, cancellationToken);
            }

            return Aggregate(
                cpfRows,
                catalogActiveTenants,
                documentPresent,
                documentPopulated,
                documentConflicts);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync();
        }
    }

    public static CatalogPreflightDiagnosticCounts Aggregate(
        IReadOnlyList<(Guid TenantId, string? Cpf)> rows,
        int catalogActiveTenants,
        bool documentColumnPresent,
        int documentPopulated,
        int documentConflicts)
    {
        var withCpf = rows.Where(row => !string.IsNullOrWhiteSpace(row.Cpf)).ToList();
        var nonDigit = withCpf.Count(row => row.Cpf!.Any(ch => !char.IsDigit(ch)));
        var lengthNot11 = withCpf.Count(row => row.Cpf!.Length != 11);
        var invalidCheck = withCpf.Count(row =>
            row.Cpf!.Length == 11
            && row.Cpf.All(char.IsDigit)
            && !BrazilianCpf.IsValidCheckDigits(row.Cpf));

        var duplicateGroups = withCpf
            .GroupBy(row => (row.TenantId, row.Cpf))
            .Where(group => group.Count() > 1)
            .ToList();

        var samples = duplicateGroups
            .Take(3)
            .Select(group => new CatalogPreflightDuplicateSample(
                group.Key.TenantId,
                BrazilianCpf.Mask(group.Key.Cpf)))
            .ToList();

        return new CatalogPreflightDiagnosticCounts(
            rows.Count,
            withCpf.Count,
            duplicateGroups.Count,
            duplicateGroups.Sum(group => group.Count()),
            nonDigit,
            lengthNot11,
            invalidCheck,
            documentColumnPresent,
            documentPopulated,
            documentConflicts,
            catalogActiveTenants,
            samples);
    }

    private static async Task<List<(Guid TenantId, string? Cpf)>> ReadCpfRowsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT tenant_id, cpf
            FROM core.customers
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(Guid TenantId, string? Cpf)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<bool> DocumentColumnExistsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'core'
                  AND table_name = 'customers'
                  AND column_name = 'document')
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result switch
        {
            true => true,
            false => false,
            int number => number != 0,
            long number => number != 0,
            _ => false,
        };
    }

    private static async Task<(int Populated, int Conflicts)> ReadDocumentStatsAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COUNT(*) FILTER (WHERE document IS NOT NULL AND btrim(document) <> '') AS populated,
                COUNT(*) FILTER (
                    WHERE document IS NOT NULL
                      AND btrim(document) <> ''
                      AND cpf IS NOT NULL
                      AND btrim(cpf) <> ''
                      AND document IS DISTINCT FROM cpf) AS conflicts
            FROM core.customers
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (0, 0);
        }

        return (Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)));
    }

    private sealed record CustomerCpfRow(Guid TenantId, string? Cpf, string? Document);
}
