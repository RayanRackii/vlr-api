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

        var tenantStoragePrefixes = StoragePrefixes(id);
        foreach (var bucket in new[]
                 {
                     storageOptions.Value.PublicBucket,
                     storageOptions.Value.PrivateBucket,
                 }.Distinct(StringComparer.Ordinal))
        {
            foreach (var tenantStoragePrefix in tenantStoragePrefixes)
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
    /// Storage prefixes owned by a tenant: the tenant root and the portal landing images
    /// (<c>tenants/{id}/landing/...</c>).
    /// </summary>
    internal static IReadOnlyList<string> StoragePrefixes(Guid tenantId) =>
        [$"{tenantId:N}/", $"tenants/{tenantId:N}/"];

    /// <summary>
    /// Include every TenantId row (including nullable claims), then its FK dependents.
    /// Global principals such as permissions and notification templates never enter the plan.
    /// Reject new cycles rather than silently leaving data behind.
    /// </summary>
    internal static IReadOnlyList<IEntityType> BuildDeletionOrder(IModel model)
    {
        var entities = model.GetEntityTypes().ToArray();
        var covered = TenantDeletionOrder.Cover(entities, entity => entity.FindProperty("TenantId") is not null);
        return TenantDeletionOrder.Order(covered);
    }

    private async Task DeleteEntityAsync(IEntityType entity, Guid tenantId, CancellationToken cancellationToken)
    {
        var sqlBuilder = new TenantScopedSql(dbContext.GetService<ISqlGenerationHelper>());
        var aliasCounter = 0;
        var scope = sqlBuilder.Scope(entity, "target", ref aliasCounter);
        var selfReferences = entity.GetForeignKeys().Where(fk => fk.PrincipalEntityType == entity).ToArray();
        var leafPredicate = string.Concat(selfReferences.Select(fk =>
            $" AND NOT EXISTS (SELECT 1 FROM {sqlBuilder.Table(entity)} AS child WHERE {sqlBuilder.Join(fk, "child", "target")})"));
        var sql = $"DELETE FROM {sqlBuilder.Table(entity)} AS target WHERE {scope}{leafPredicate}";
        int deleted;
        do
        {
            deleted = await dbContext.Database.ExecuteSqlRawAsync(sql, new object[] { tenantId }, cancellationToken);
        } while (selfReferences.Length > 0 && deleted > 0);

        if (selfReferences.Length > 0)
        {
            // Interpolated segments are schema-derived identifiers (table/alias names), not user
            // input; tenantId is passed as a real SqlQueryRaw parameter, not interpolated. False
            // positive surfaced by -warnaserror once this file is recompiled (pre-existing, unrelated
            // to resource planning).
#pragma warning disable EF1002
            var count = await dbContext.Database.SqlQueryRaw<int>(
                    $"SELECT CAST(COUNT(*) AS INTEGER) AS {dbContext.GetService<ISqlGenerationHelper>().DelimitIdentifier("Value")} FROM {sqlBuilder.Table(entity)} AS target WHERE {scope}", tenantId)
                .SingleAsync(cancellationToken);
#pragma warning restore EF1002
            if (count > 0)
                throw new InvalidOperationException($"Cannot purge cyclic or externally referenced rows in {entity.Name}.");
        }
    }
}
