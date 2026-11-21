using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Platform.Api.Services.Tenancy;

/// <summary>
/// Leaf-first deletion order over a candidate set of entities, shared by <see cref="TenantPurger"/>
/// (the whole model) and <see cref="ModuleDataScope"/> (one module's schema). Foreign keys that
/// point outside the covered set are ignored: they are the caller's concern (blockers).
/// </summary>
internal static class TenantDeletionOrder
{
    /// <summary>
    /// Seeds (entities for which <paramref name="isSeed"/> holds) plus, repeatedly, every pool entity
    /// with a foreign key into the covered set.
    /// </summary>
    internal static HashSet<IEntityType> Cover(IReadOnlyCollection<IEntityType> pool, Func<IEntityType, bool> isSeed)
    {
        var covered = pool.Where(isSeed).ToHashSet();
        while (true)
        {
            var dependents = pool.Where(entity => !covered.Contains(entity)
                && entity.GetForeignKeys().Any(fk => covered.Contains(fk.PrincipalEntityType))).ToArray();
            if (dependents.Length == 0)
                break;
            covered.UnionWith(dependents);
        }

        return covered;
    }

    /// <summary>
    /// Validates the covered set and returns it leaf-first (dependents before principals, ties by
    /// entity name). Rejects new cycles rather than silently leaving data behind.
    /// </summary>
    internal static IReadOnlyList<IEntityType> Order(IReadOnlyCollection<IEntityType> covered)
    {
        foreach (var entity in covered)
        {
            var tenantProperty = entity.FindProperty("TenantId");
            if (tenantProperty is null && entity.GetForeignKeys().Any(fk => fk.PrincipalEntityType == entity))
                throw new InvalidOperationException($"Unsupported unscoped self-reference in tenant purge: {entity.Name}.");
            if (entity.GetTableName() is null
                || (tenantProperty is not null && tenantProperty.ClrType != typeof(Guid)
                    && tenantProperty.ClrType != typeof(Guid?)))
                throw new InvalidOperationException($"Unsupported tenant purge mapping: {entity.Name}.");
        }

        var remaining = covered.ToHashSet();
        var result = new List<IEntityType>();
        while (remaining.Count > 0)
        {
            var leaves = remaining.Where(entity => !remaining.Any(other => other != entity
                    && other.GetForeignKeys().Any(fk => fk.PrincipalEntityType == entity)))
                .OrderBy(entity => entity.Name, StringComparer.Ordinal).ToArray();
            if (leaves.Length == 0)
                throw new InvalidOperationException("Tenant purge FK cycle: "
                    + string.Join(", ", remaining.Select(entity => entity.Name)));
            result.AddRange(leaves);
            remaining.ExceptWith(leaves);
        }

        return result;
    }
}
