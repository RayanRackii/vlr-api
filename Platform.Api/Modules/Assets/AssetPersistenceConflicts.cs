using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Platform.Api.Modules.Assets;

internal static class AssetPersistenceConflicts
{
    public static bool IsTagUniqueViolation(DbUpdateException exception)
    {
        var postgres = FindPostgres(exception);
        return postgres?.SqlState == PostgresErrorCodes.UniqueViolation
            && Mentions(postgres, "ix_assets_tenant_id_tag");
    }

    public static bool IsForeignKeyViolation(DbUpdateException exception) =>
        FindPostgres(exception)?.SqlState == PostgresErrorCodes.ForeignKeyViolation;

    public static bool IsWorkOrderAssetForeignKey(DbUpdateException exception)
    {
        var postgres = FindPostgres(exception);
        return postgres?.SqlState == PostgresErrorCodes.ForeignKeyViolation
            && Mentions(postgres, "work_order")
            && Mentions(postgres, "asset");
    }

    private static bool Mentions(PostgresException postgres, string token)
    {
        var blob = string.Join(
            ' ',
            postgres.ConstraintName,
            postgres.TableName,
            postgres.Detail,
            postgres.MessageText);
        return blob.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    private static PostgresException? FindPostgres(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }
}
