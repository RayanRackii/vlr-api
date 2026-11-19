using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Platform.Api.Modules.ResourcePlanning.Services;

/// <summary>
/// Maps a failed planning save to the right planning error. Only a real concurrency or unique-key
/// conflict is a retryable <c>planning.version_conflict</c>; a foreign-key violation means the
/// asset vanished under the caller; anything else is a bug or an outage and must not be disguised
/// as a conflict.
/// </summary>
internal static class PlanningPersistenceErrors
{
    private const string UniqueViolation = "23505";
    private const string ForeignKeyViolation = "23503";

    /// <summary>The planning error for the failure, or null when it must propagate unchanged.</summary>
    public static PlanningException? Translate(Exception exception, Guid assetId)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return PlanningException.VersionConflict(
                "The record was changed concurrently; reload and try again.");
        }

        if (exception is not DbUpdateException)
        {
            return null;
        }

        var postgres = FindPostgres(exception);
        return postgres?.SqlState switch
        {
            UniqueViolation => PlanningException.VersionConflict(
                "The record was changed concurrently; reload and try again."),
            ForeignKeyViolation => PlanningException.AssetNotFound(assetId),
            _ => null,
        };
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
