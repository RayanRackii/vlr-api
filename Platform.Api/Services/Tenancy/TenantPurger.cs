using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Platform.Api.Authentication;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;
using Platform.Core.Infrastructure.Supabase;

namespace Platform.Api.Services.Tenancy;

public interface ITenantPurger
{
    Task PurgeAsync(Guid tenantId, CancellationToken cancellationToken);
}

public sealed class TenantPurger(
    AppDbContext dbContext,
    ISupabaseAuthAdminClient supabaseAuthAdminClient,
    IOptions<PlatformAdminOptions> platformAdminOptions,
    ILogger<TenantPurger> logger,
    IOptions<StorageOptions> storageOptions,
    IStorageProvider storageProvider) : ITenantPurger
{
    public async Task PurgeAsync(Guid id, CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "admin-tenant-delete");
        List<string> supabaseAuthIds;

        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                if (dbContext.Database.IsNpgsql())
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT id FROM core.tenants WHERE id = {id} FOR UPDATE", cancellationToken);
                var tenantExists = await dbContext.Tenants
                    .AnyAsync(t => t.Id == id, cancellationToken);

                if (!tenantExists)
                {
                    throw new KeyNotFoundException("Tenant not found.");
                }

                // Users are tenant-scoped. After enter, the ambient filter would hide the
                // target tenant's memberships and any other tenant that still shares an auth id.
                var tenantUsers = await dbContext.Users
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(u => u.TenantId == id)
                    .Select(u => new { u.SupabaseAuthId, u.Email })
                    .ToListAsync(cancellationToken);

                var candidateAuthIds = tenantUsers
                    .Select(u => u.SupabaseAuthId)
                    .Where(authId => !string.IsNullOrWhiteSpace(authId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var platformAdminEmails = platformAdminOptions.Value.Emails
                    .Where(e => !string.IsNullOrWhiteSpace(e))
                    .Select(e => e.Trim().ToLowerInvariant())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var authIdsStillUsedElsewhere = candidateAuthIds.Count == 0
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : (await dbContext.Users
                        .AsNoTracking()
                        .IgnoreQueryFilters()
                        .Where(u =>
                            u.TenantId != id
                            && candidateAuthIds.Contains(u.SupabaseAuthId))
                        .Select(u => u.SupabaseAuthId)
                        .Distinct()
                        .ToListAsync(cancellationToken))
                        .ToHashSet(StringComparer.Ordinal);

                var platformAdminAuthIds = tenantUsers
                    .Where(u => platformAdminEmails.Contains(u.Email.Trim().ToLowerInvariant()))
                    .Select(u => u.SupabaseAuthId)
                    .ToHashSet(StringComparer.Ordinal);

                supabaseAuthIds = candidateAuthIds
                    .Where(authId =>
                        !authIdsStillUsedElsewhere.Contains(authId)
                        && !platformAdminAuthIds.Contains(authId))
                    .ToList();

                // Both scope and FK ordering come from the same EF model used by persistence.
                var plan = BuildDeletionOrder(dbContext.Model);
                foreach (var entity in plan)
                {
                    await DeleteEntityAsync(entity, id, cancellationToken);
                }

                await dbContext.Tenants
                    .Where(t => t.Id == id)
                    .ExecuteDeleteAsync(cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException(
                    "Cannot delete this tenant because it still has linked data.",
                    ex);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        var tenantStoragePrefix = $"{id:N}/";
        foreach (var bucket in new[]
                 {
                     storageOptions.Value.PublicBucket,
                     storageOptions.Value.PrivateBucket,
                 }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await storageProvider.DeletePrefixAsync(bucket, tenantStoragePrefix, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Tenant storage purge failed after database deletion. Bucket: {Bucket}; Prefix: {Prefix}",
                    bucket,
                    tenantStoragePrefix);
            }
        }

        foreach (var authId in supabaseAuthIds)
        {
            try
            {
                await supabaseAuthAdminClient.DeleteUserAsync(authId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Tenant {TenantId} deleted, but Supabase auth delete failed for {SupabaseAuthId}.",
                    id,
                    authId);
            }
        }
    }


    /// <summary>
    /// Include every TenantId row (including nullable claims), then its FK dependents.
    /// Global principals such as permissions and notification templates never enter the plan.
    /// Reject new cycles rather than silently leaving data behind.
    /// </summary>
    internal static IReadOnlyList<IEntityType> BuildDeletionOrder(IModel model)
    {
        var entities = model.GetEntityTypes().ToArray();
        var covered = entities.Where(entity => entity.FindProperty("TenantId") is not null).ToHashSet();
        while (true)
        {
            var dependents = entities.Where(entity => !covered.Contains(entity)
                && entity.GetForeignKeys().Any(fk => covered.Contains(fk.PrincipalEntityType))).ToArray();
            if (dependents.Length == 0)
                break;
            covered.UnionWith(dependents);
        }

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

    private async Task DeleteEntityAsync(IEntityType entity, Guid tenantId, CancellationToken cancellationToken)
    {
        var aliasCounter = 0;
        var scope = Scope(entity, "target", ref aliasCounter);
        var selfReferences = entity.GetForeignKeys().Where(fk => fk.PrincipalEntityType == entity).ToArray();
        var leafPredicate = string.Concat(selfReferences.Select(fk =>
            $" AND NOT EXISTS (SELECT 1 FROM {Table(entity)} AS child WHERE {Join(fk, "child", "target")})"));
        var sql = $"DELETE FROM {Table(entity)} AS target WHERE {scope}{leafPredicate}";
        int deleted;
        do
        {
            deleted = await dbContext.Database.ExecuteSqlRawAsync(sql, new object[] { tenantId }, cancellationToken);
        } while (selfReferences.Length > 0 && deleted > 0);

        if (selfReferences.Length > 0)
        {
            var count = await dbContext.Database.SqlQueryRaw<int>(
                    $"SELECT CAST(COUNT(*) AS INTEGER) AS {dbContext.GetService<ISqlGenerationHelper>().DelimitIdentifier("Value")} FROM {Table(entity)} AS target WHERE {scope}", tenantId)
                .SingleAsync(cancellationToken);
            if (count > 0)
                throw new InvalidOperationException($"Cannot purge cyclic or externally referenced rows in {entity.Name}.");
        }
    }

    private string Scope(IEntityType entity, string alias, ref int aliasCounter)
    {
        var tenantId = entity.FindProperty("TenantId");
        if (tenantId is not null)
            return $"{alias}.{Column(entity, tenantId)} = {{0}}";

        // An unscoped dependent belongs to the tenant through its scoped principal.
        var predicates = new List<string>();
        foreach (var fk in entity.GetForeignKeys())
        {
            var principal = fk.PrincipalEntityType;
            if (!HasTenantAncestor(principal, []))
                continue;
            var parentAlias = $"parent{++aliasCounter}";
            var parentScope = Scope(principal, parentAlias, ref aliasCounter);
            predicates.Add($"EXISTS (SELECT 1 FROM {Table(principal)} AS {parentAlias} "
                + $"WHERE {Join(fk, alias, parentAlias)} AND ({parentScope}))");
        }
        if (predicates.Count == 0)
            throw new InvalidOperationException($"No tenant scope for purge entity {entity.Name}.");
        return "(" + string.Join(" OR ", predicates) + ")";
    }

    private static bool HasTenantAncestor(IEntityType entity, HashSet<IEntityType> visited)
    {
        if (entity.FindProperty("TenantId") is not null)
            return true;
        return visited.Add(entity) && entity.GetForeignKeys()
            .Any(fk => HasTenantAncestor(fk.PrincipalEntityType, visited));
    }

    private string Join(IForeignKey foreignKey, string dependentAlias, string principalAlias) =>
        string.Join(" AND ", foreignKey.Properties.Select((property, index) =>
            $"{dependentAlias}.{Column(foreignKey.DeclaringEntityType, property)} = "
            + $"{principalAlias}.{Column(foreignKey.PrincipalEntityType, foreignKey.PrincipalKey.Properties[index])}"));

    private string Table(IEntityType entity) => dbContext.GetService<ISqlGenerationHelper>()
        .DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());

    private string Column(IEntityType entity, IProperty property) => dbContext.GetService<ISqlGenerationHelper>()
        .DelimitIdentifier(property.GetColumnName(StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema()))!);
}
