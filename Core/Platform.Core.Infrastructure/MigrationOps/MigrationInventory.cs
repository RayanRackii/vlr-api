namespace Platform.Core.Infrastructure.MigrationOps;

public static class MigrationInventory
{
    public static IReadOnlyList<string> ComputePending(
        IReadOnlyList<string> assemblyMigrations,
        IReadOnlyList<string> appliedMigrations)
    {
        ArgumentNullException.ThrowIfNull(assemblyMigrations);
        ArgumentNullException.ThrowIfNull(appliedMigrations);

        var applied = appliedMigrations.ToHashSet(StringComparer.Ordinal);
        return assemblyMigrations
            .Where(migration => !applied.Contains(migration))
            .ToList();
    }

    public static string? LastApplied(IReadOnlyList<string> appliedMigrations) =>
        appliedMigrations.Count == 0 ? null : appliedMigrations[^1];
}
