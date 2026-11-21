using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Modules.Admin.Services;
using Platform.Api.Services.Tenancy;
using Platform.Api.Storage;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

/// <summary>
/// Daily executor of the scheduled module data purges (spec slice API-3, amendments 7.1 to 7.3).
/// For each purge that is due: lock the tenant row, re-check under the lock (module still off, no
/// blockers, tenant still there), delete the module's rows in <see cref="ModuleDataScope"/> order
/// inside ONE transaction, in batches, then delete the storage objects collected before the delete.
/// A purge that fails its re-check, hits the command timeout or a foreign key is cancelled with a
/// reason, never half applied. Only counts are stored and logged, never row contents.
/// </summary>
public sealed class ModuleDataPurgeJob(
    AppDbContext dbContext,
    IModuleDataPurgeService dataPurge,
    IStorageProvider storageProvider,
    IOptions<StorageOptions> storageOptions,
    ILogger<ModuleDataPurgeJob> logger)
{
    public const string TenantDeletedReason = "tenant.deleted";
    public const string TimeoutReason = "timeout";
    public const string ForeignKeyViolationReason = "fk.violation";

    /// <summary>Rows deleted per statement (amendment 7.2).</summary>
    internal int BatchSize { get; init; } = 5000;

    /// <summary>Timeout of every command of the purge transaction (amendment 7.2).</summary>
    internal TimeSpan CommandTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        using var _ = PlatformScope.Begin(logger, "module-data-purge");

        var now = DateTimeOffset.UtcNow;
        var dueIds = await dbContext.TenantModuleDataPurges
            .AsNoTracking()
            .Where(p => p.CancelledAt == null && p.ExecutedAt == null && p.ScheduledFor <= now)
            .OrderBy(p => p.ScheduledFor)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        if (dueIds.Count == 0)
        {
            logger.LogInformation("No module data purge is due.");
            return;
        }

        logger.LogInformation("{Count} module data purge(s) due.", dueIds.Count);
        foreach (var purgeId in dueIds)
        {
            try
            {
                await ExecuteOneAsync(purgeId, now, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Left pending: it is retried by the next run. The message never carries row contents.
                logger.LogError(ex, "Module data purge {PurgeId} failed and stays pending.", purgeId);
            }
        }
    }

    private async Task ExecuteOneAsync(Guid purgeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var tenantId = await dbContext.TenantModuleDataPurges
            .AsNoTracking()
            .Where(p => p.Id == purgeId)
            .Select(p => (Guid?)p.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantId is null)
            return;

        string? failureReason = null;
        ExecutedPurge? executed = null;
        var deleting = false;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                // Same order as AdminTenantService.UpdateAsync: advisory lock, then the tenant row. A
                // re-enable that committed first has already cancelled the purge, and we see that below.
                await TenantModuleLocks.LockAsync(dbContext, tenantId.Value, cancellationToken);

                dbContext.ChangeTracker.Clear();
                var purge = await dbContext.TenantModuleDataPurges
                    .AsTracking()
                    .FirstOrDefaultAsync(
                        p => p.Id == purgeId && p.CancelledAt == null && p.ExecutedAt == null,
                        cancellationToken);
                if (purge is null || purge.ScheduledFor > now)
                {
                    // Cancelled, executed, or deleted together with its tenant (TenantPurger) meanwhile.
                    return;
                }

                var tenantExists = await dbContext.Tenants
                    .AsNoTracking()
                    .AnyAsync(t => t.Id == tenantId.Value, cancellationToken);
                if (!tenantExists)
                {
                    await CancelInTransactionAsync(purge, TenantDeletedReason, transaction, cancellationToken);
                    return;
                }

                ModuleDataPurgePreviewDto preview;
                try
                {
                    preview = await dataPurge.PreviewAsync(tenantId.Value, purge.Module, cancellationToken);
                }
                catch (ModuleDataPurgeRequestException ex)
                {
                    await CancelInTransactionAsync(purge, ex.Code, transaction, cancellationToken);
                    return;
                }

                if (preview.Blockers.Count > 0)
                {
                    await CancelInTransactionAsync(purge, BlockerReason(preview), transaction, cancellationToken);
                    return;
                }

                deleting = true;
                executed = await DeleteModuleDataAsync(purge, tenantId.Value, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            // Only the delete phase can mean "too big for the timeout"; a slow lock wait must not cancel the purge.
            catch (Exception ex) when (deleting && IsTimeout(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                failureReason = TimeoutReason;
            }
            catch (Exception ex) when (deleting && IsForeignKeyViolation(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                failureReason = ForeignKeyViolationReason;
            }
            finally
            {
                dbContext.Database.SetCommandTimeout((TimeSpan?)null);
            }
        }

        if (failureReason is not null)
        {
            await CancelAfterRollbackAsync(purgeId, failureReason, cancellationToken);
            logger.LogWarning(
                "Module data purge {PurgeId} cancelled ({Reason}); nothing was deleted.",
                purgeId,
                failureReason);
            return;
        }

        if (executed is null)
            return;

        logger.LogInformation(
            "Module data purge {PurgeId} executed for tenant {TenantId}, module {Module}: {Rows} row(s) in {Tables} table(s), {Keys} storage object(s) to delete.",
            purgeId,
            tenantId,
            executed.Module,
            executed.Tables.Values.Sum(),
            executed.Tables.Count,
            executed.StorageKeys.Count);

        await DeleteStorageObjectsAsync(purgeId, executed, cancellationToken);
    }

    private async Task<ExecutedPurge> DeleteModuleDataAsync(
        TenantModuleDataPurge purge,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var plan = ModuleDataScope.PlanFor(dbContext.Model, purge.Module);
        var storageKeys = await CollectStorageKeysAsync(plan, tenantId, cancellationToken);

        dbContext.Database.SetCommandTimeout(CommandTimeout);
        var tables = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var entry in plan)
        {
            var deleted = await DeleteEntryAsync(entry, tenantId, cancellationToken);
            if (deleted > 0)
                tables[ModuleDataPurgeService.QualifiedName(entry.Entity)] = deleted;
        }

        purge.ExecutedAt = DateTimeOffset.UtcNow;
        purge.ResultJson = JsonSerializer.Serialize(new
        {
            tables,
            storageObjects = storageKeys.Count,
        });
        purge.StampConcurrency();
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ExecutedPurge(purge.Module, tables, storageKeys);
    }

    /// <summary>
    /// Deletes the tenant's rows of one entity in batches (one statement per batch, inside the open
    /// transaction). Self-referencing tables delete leaves first and repeat until nothing is left.
    /// </summary>
    private async Task<long> DeleteEntryAsync(ModuleScopeEntry entry, Guid tenantId, CancellationToken cancellationToken)
    {
        var entity = entry.Entity;
        var sql = new TenantScopedSql(dbContext.GetService<ISqlGenerationHelper>());
        var aliasCounter = 0;
        var scope = sql.Scope(entity, "target", ref aliasCounter);
        var filter = ModuleDataPurgeService.FilterPredicate(sql, entity, entry.RowFilter, "target");
        if (filter is not null)
            scope += $" AND {filter}";

        var table = sql.Table(entity);
        var selfReferences = entity.GetForeignKeys().Where(fk => fk.PrincipalEntityType == entity).ToArray();
        var leafPredicate = string.Concat(selfReferences.Select(fk =>
            $" AND NOT EXISTS (SELECT 1 FROM {table} AS child WHERE {sql.Join(fk, "child", "target")})"));

        // ctid = ANY (ARRAY(... LIMIT n)) is the Postgres way to batch a DELETE; the selected rows are
        // exactly the tenant's rows in scope. Identifiers come from the EF model, the tenant id is a parameter.
        var batchStatement =
            $"DELETE FROM {table} AS doomed WHERE doomed.ctid = ANY (ARRAY(SELECT target.ctid FROM {table} AS target "
            + $"WHERE {scope}{leafPredicate} LIMIT {BatchSize}))";

#pragma warning disable EF1002
        long total = 0;
        int deleted;
        do
        {
            deleted = await dbContext.Database.ExecuteSqlRawAsync(
                batchStatement,
                new object[] { tenantId },
                cancellationToken);
            total += deleted;
        }
        while (selfReferences.Length > 0 ? deleted > 0 : deleted >= BatchSize);

        if (selfReferences.Length > 0)
        {
            var remainingStatement =
                $"SELECT COUNT(*) AS {sql.Delimit("Value")} FROM {table} AS target WHERE {scope}";
#pragma warning disable EF1002
            var remaining = await dbContext.Database
                .SqlQueryRaw<long>(remainingStatement, tenantId)
                .SingleAsync(cancellationToken);
#pragma warning restore EF1002
            if (remaining > 0)
                throw new InvalidOperationException($"Cannot purge cyclic or externally referenced rows in {entity.Name}.");
        }

#pragma warning restore EF1002

        return total;
    }

    /// <summary>
    /// The storage keys that rows in scope point at (catalog product files, product request files, work
    /// order task files), read before the delete so they can be removed after commit. Legacy public
    /// catalog files live in the public bucket, everything else in the private one.
    /// </summary>
    private async Task<IReadOnlyList<StorageObjectRef>> CollectStorageKeysAsync(
        IReadOnlyList<ModuleScopeEntry> plan,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var options = storageOptions.Value;
        var keys = new HashSet<StorageObjectRef>();
        var inScope = plan.Select(entry => entry.Entity.ClrType).ToHashSet();

        if (inScope.Contains(typeof(CatalogProductFile)))
        {
            var files = await dbContext.CatalogProductFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(file => file.TenantId == tenantId)
                .Select(file => new { file.StorageKey, file.IsLegacyPublic })
                .ToListAsync(cancellationToken);
            foreach (var file in files)
                keys.Add(new StorageObjectRef(file.IsLegacyPublic ? options.PublicBucket : options.PrivateBucket, file.StorageKey));
        }

        if (inScope.Contains(typeof(ProductRequestFile)))
        {
            var files = await dbContext.ProductRequestFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(file => file.TenantId == tenantId)
                .Select(file => file.StorageKey)
                .ToListAsync(cancellationToken);
            foreach (var key in files)
                keys.Add(new StorageObjectRef(options.PrivateBucket, key));
        }

        if (inScope.Contains(typeof(WorkOrderTaskFile)))
        {
            var files = await dbContext.WorkOrderTaskFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(file => file.TenantId == tenantId)
                .Select(file => file.StorageKey)
                .ToListAsync(cancellationToken);
            foreach (var key in files)
                keys.Add(new StorageObjectRef(options.PrivateBucket, key));
        }

        return keys.ToList();
    }

    /// <summary>Best effort after the commit: a failure is counted and logged, never fatal.</summary>
    private async Task DeleteStorageObjectsAsync(Guid purgeId, ExecutedPurge executed, CancellationToken cancellationToken)
    {
        if (executed.StorageKeys.Count == 0)
            return;

        var deleted = 0;
        var failed = 0;
        foreach (var storageObject in executed.StorageKeys)
        {
            try
            {
                await storageProvider.DeleteAsync(storageObject.Bucket, storageObject.Key, cancellationToken);
                deleted++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                logger.LogWarning(
                    "Storage delete failed after module data purge {PurgeId}. Bucket: {Bucket}",
                    purgeId,
                    storageObject.Bucket);
            }
        }

        try
        {
            dbContext.ChangeTracker.Clear();
            var purge = await dbContext.TenantModuleDataPurges
                .AsTracking()
                .FirstOrDefaultAsync(p => p.Id == purgeId, cancellationToken);
            if (purge is null)
                return;

            purge.ResultJson = JsonSerializer.Serialize(new
            {
                tables = executed.Tables,
                storageObjects = executed.StorageKeys.Count,
                storageDeleted = deleted,
                storageFailed = failed,
            });
            purge.StampConcurrency();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not record the storage result of module data purge {PurgeId}.", purgeId);
        }
    }

    private async Task CancelInTransactionAsync(
        TenantModuleDataPurge purge,
        string reason,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        purge.Cancel(DateTimeOffset.UtcNow, null, reason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogWarning(
            "Module data purge {PurgeId} cancelled ({Reason}); nothing was deleted.",
            purge.Id,
            reason);
    }

    private async Task CancelAfterRollbackAsync(Guid purgeId, string reason, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        var purge = await dbContext.TenantModuleDataPurges
            .AsTracking()
            .FirstOrDefaultAsync(
                p => p.Id == purgeId && p.CancelledAt == null && p.ExecutedAt == null,
                cancellationToken);
        if (purge is null)
            return;

        try
        {
            purge.Cancel(DateTimeOffset.UtcNow, null, reason);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else (a re-enable) cancelled it first: the outcome is the same.
            dbContext.ChangeTracker.Clear();
        }
    }

    private static string BlockerReason(ModuleDataPurgePreviewDto preview)
    {
        var reason = "blocked: " + string.Join(
            "; ",
            preview.Blockers.Select(blocker => blocker.Table is null ? blocker.Code : $"{blocker.Code} {blocker.Table}"));
        return reason.Length <= TenantModuleDataPurge.CancelReasonMaxLength
            ? reason
            : reason[..TenantModuleDataPurge.CancelReasonMaxLength];
    }

    private static bool IsTimeout(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException
                || current is PostgresException { SqlState: PostgresErrorCodes.QueryCanceled })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForeignKeyViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
                return true;
        }

        return false;
    }

    private sealed record StorageObjectRef(string Bucket, string Key);

    private sealed record ExecutedPurge(
        string Module,
        SortedDictionary<string, long> Tables,
        IReadOnlyList<StorageObjectRef> StorageKeys);
}
