using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Platform.Api.Modules.Admin.Dtos;
using Platform.Api.Services.Tenancy;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Admin.Services;

public interface IModuleDataPurgeService
{
    /// <summary>
    /// Counts what purging <paramref name="module"/> would delete for the tenant and lists the blockers.
    /// Throws <see cref="ModuleDataPurgeRequestException"/> for an unknown or non-purgeable module and
    /// <see cref="KeyNotFoundException"/> for an unknown tenant.
    /// </summary>
    Task<ModuleDataPurgePreviewDto> PreviewAsync(
        Guid tenantId,
        string? module,
        CancellationToken cancellationToken);

    /// <summary>
    /// Schedules the purge 30 days ahead. Throws <see cref="ModuleDataPurgeRequestException"/> (bad module
    /// or confirmation), <see cref="KeyNotFoundException"/> (unknown tenant),
    /// <see cref="ModuleDataPurgeBlockedException"/> (blockers; carries the preview) and
    /// <see cref="ModuleDataPurgeConflictException"/> (already pending).
    /// </summary>
    Task<ModuleDataPurgeScheduledDto> ScheduleAsync(
        Guid tenantId,
        string? module,
        string? confirm,
        string? actor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Cancels the pending purge and records who/when/why on its row. Throws
    /// <see cref="KeyNotFoundException"/> when the tenant or the pending purge does not exist.
    /// </summary>
    Task CancelAsync(
        Guid tenantId,
        string? module,
        string? reason,
        string? actor,
        CancellationToken cancellationToken);
}

/// <summary>
/// The request is invalid; <see cref="Code"/> is <c>module.unknown</c>, <c>module.not_purgeable</c>,
/// <c>confirm.mismatch</c> or <c>reason.too_long</c>.
/// </summary>
public sealed class ModuleDataPurgeRequestException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The purge cannot be scheduled right now; <see cref="Preview"/> lists why.</summary>
public sealed class ModuleDataPurgeBlockedException(ModuleDataPurgePreviewDto preview)
    : Exception("The module data purge is blocked.")
{
    public ModuleDataPurgePreviewDto Preview { get; } = preview;
}

/// <summary>A purge is already pending for this tenant and module (<c>purge.already_pending</c>).</summary>
public sealed class ModuleDataPurgeConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Module data purge: read-only preview (slice API-2), schedule and cancel (slice API-3). Counts use raw
/// SQL generated from the EF model with an explicit tenant parameter, so they never depend on the
/// ambient tenant query filter (same SQL builder as <see cref="TenantPurger"/>) and never log or return
/// row contents. The daily <c>ModuleDataPurgeJob</c> executes what is scheduled here.
/// </summary>
public sealed class ModuleDataPurgeService(
    AppDbContext dbContext,
    ILogger<ModuleDataPurgeService> logger) : IModuleDataPurgeService
{
    public const string ModuleActiveCode = "module.active";
    public const string ForeignKeyRestrictCode = "fk.restrict";
    public const string ForeignKeyCascadeCode = "fk.cascade";
    public const string UnreleasedAllocationsCode = "allocations.unreleased";
    public const string ConfirmMismatchCode = "confirm.mismatch";
    public const string ReasonTooLongCode = "reason.too_long";
    public const string AlreadyPendingCode = "purge.already_pending";

    private static readonly AllocationStatus[] UnreleasedStatuses =
    [
        AllocationStatus.Scheduled,
        AllocationStatus.Running,
        AllocationStatus.Cleaning,
    ];

    public async Task<ModuleDataPurgePreviewDto> PreviewAsync(
        Guid tenantId,
        string? module,
        CancellationToken cancellationToken)
    {
        var canonical = ResolveOrThrow(module);

        using var _ = PlatformScope.Begin(logger, "module-data-purge-preview");

        await EnsureTenantExistsAsync(tenantId, cancellationToken);
        return await BuildPreviewAsync(tenantId, canonical, cancellationToken);
    }

    public async Task<ModuleDataPurgeScheduledDto> ScheduleAsync(
        Guid tenantId,
        string? module,
        string? confirm,
        string? actor,
        CancellationToken cancellationToken)
    {
        var canonical = ResolveOrThrow(module);
        if (!string.Equals(confirm?.Trim(), canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModuleDataPurgeRequestException(
                ConfirmMismatchCode,
                "Type the module key to confirm the purge.");
        }

        using var _ = PlatformScope.Begin(logger, "module-data-purge-schedule");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await TenantModuleLocks.LockAsync(dbContext, tenantId, cancellationToken);
            await EnsureTenantExistsAsync(tenantId, cancellationToken);

            // Re-read everything under the lock: a re-enable or another schedule may have just committed.
            var preview = await BuildPreviewAsync(tenantId, canonical, cancellationToken);
            if (preview.Pending is not null)
            {
                throw new ModuleDataPurgeConflictException(
                    AlreadyPendingCode,
                    "A purge is already scheduled for this module.");
            }

            if (preview.Blockers.Count > 0)
                throw new ModuleDataPurgeBlockedException(preview);

            var now = DateTimeOffset.UtcNow;
            var purge = new TenantModuleDataPurge
            {
                TenantId = tenantId,
                Module = canonical,
                RequestedByUserId = Trim(actor, TenantModuleDataPurge.ActorMaxLength),
                RequestedAt = now,
                ScheduledFor = now + TenantModuleDataPurge.GracePeriod,
            };
            dbContext.TenantModuleDataPurges.Add(purge);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Module data purge {PurgeId} scheduled for tenant {TenantId}, module {Module}, at {ScheduledFor}.",
                purge.Id,
                tenantId,
                canonical,
                purge.ScheduledFor);
            return new ModuleDataPurgeScheduledDto(purge.Id, canonical, purge.RequestedAt, purge.ScheduledFor);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The partial unique index is the last line of defence against a concurrent schedule.
            throw new ModuleDataPurgeConflictException(
                AlreadyPendingCode,
                "A purge is already scheduled for this module.");
        }
    }

    public async Task CancelAsync(
        Guid tenantId,
        string? module,
        string? reason,
        string? actor,
        CancellationToken cancellationToken)
    {
        var canonical = ResolveOrThrow(module);
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalizedReason is { Length: > TenantModuleDataPurge.CancelReasonMaxLength })
        {
            throw new ModuleDataPurgeRequestException(
                ReasonTooLongCode,
                $"The reason can have at most {TenantModuleDataPurge.CancelReasonMaxLength} characters.");
        }

        using var _ = PlatformScope.Begin(logger, "module-data-purge-cancel");

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await TenantModuleLocks.LockAsync(dbContext, tenantId, cancellationToken);
        await EnsureTenantExistsAsync(tenantId, cancellationToken);

        var purge = await dbContext.TenantModuleDataPurges
            .AsTracking()
            .FirstOrDefaultAsync(
                p => p.TenantId == tenantId
                    && p.Module == canonical
                    && p.CancelledAt == null
                    && p.ExecutedAt == null,
                cancellationToken)
            ?? throw new KeyNotFoundException("No pending purge for this module.");

        purge.Cancel(DateTimeOffset.UtcNow, Trim(actor, TenantModuleDataPurge.ActorMaxLength), normalizedReason);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Module data purge {PurgeId} cancelled for tenant {TenantId}, module {Module}.",
            purge.Id,
            tenantId,
            canonical);
    }

    /// <summary>
    /// Cancels every pending purge of the tenant for the given modules, with a system reason, in the
    /// caller's transaction (the caller saves). Used when a module is switched back on. The caller must
    /// hold <see cref="TenantModuleLocks"/> so a job cannot start deleting meanwhile.
    /// </summary>
    internal static async Task<int> CancelPendingAsync(
        AppDbContext dbContext,
        Guid tenantId,
        IEnumerable<string> modules,
        string reason,
        CancellationToken cancellationToken)
    {
        var canonical = modules
            .Select(module => PlatformModules.TryNormalize(module, out var key) ? key : module)
            .ToList();
        if (canonical.Count == 0)
            return 0;

        var pending = await dbContext.TenantModuleDataPurges
            .AsTracking()
            .Where(p => p.TenantId == tenantId
                && p.CancelledAt == null
                && p.ExecutedAt == null
                && canonical.Contains(p.Module))
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var purge in pending)
            purge.Cancel(now, null, reason);
        return pending.Count;
    }

    private static string ResolveOrThrow(string? module)
    {
        var (canonical, errorCode) = ModuleDataScope.Resolve(module);
        if (canonical is null || errorCode is not null)
        {
            var code = errorCode ?? ModuleDataScope.UnknownModuleCode;
            throw new ModuleDataPurgeRequestException(
                code,
                code == ModuleDataScope.NotPurgeableCode
                    ? "This module has no purgeable data."
                    : "Unknown module.");
        }

        return canonical;
    }

    private static string? Trim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }

    private async Task EnsureTenantExistsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenantExists = await dbContext.Tenants
            .AsNoTracking()
            .AnyAsync(t => t.Id == tenantId, cancellationToken);
        if (!tenantExists)
            throw new KeyNotFoundException("Tenant not found.");
    }

    /// <summary>
    /// The preview for an already resolved module key, run on the caller's connection and transaction
    /// (the job calls it under the tenant lock). Does not check that the tenant exists.
    /// </summary>
    internal async Task<ModuleDataPurgePreviewDto> BuildPreviewAsync(
        Guid tenantId,
        string canonical,
        CancellationToken cancellationToken)
    {
        var model = dbContext.Model;
        var sql = new TenantScopedSql(dbContext.GetService<ISqlGenerationHelper>());
        var plan = ModuleDataScope.PlanFor(model, canonical);

        var moduleActive = await IsModuleActiveAsync(model, sql, tenantId, canonical, cancellationToken);

        var tables = new List<ModuleDataPurgeTableDto>();
        foreach (var entry in plan)
        {
            var where = sql.Scope(entry.Entity, "target");
            var filter = FilterPredicate(sql, entry.Entity, entry.RowFilter, "target");
            if (filter is not null)
                where += $" AND {filter}";

            var rows = await ScalarAsync(
                sql,
                "COUNT(*)",
                $"{sql.Table(entry.Entity)} AS target WHERE {where}",
                cancellationToken,
                tenantId);
            if (rows > 0)
                tables.Add(new ModuleDataPurgeTableDto(QualifiedName(entry.Entity), rows));
        }

        var storageObjects = await CountStorageObjectsAsync(sql, plan, tenantId, cancellationToken);

        var blockers = new List<ModuleDataPurgeBlockerDto>();
        if (moduleActive)
            blockers.Add(new ModuleDataPurgeBlockerDto(ModuleActiveCode, null, null));
        blockers.AddRange(await ForeignKeyBlockersAsync(model, sql, plan, tenantId, cancellationToken));
        blockers.AddRange(await UnreleasedAllocationBlockersAsync(sql, plan, canonical, tenantId, cancellationToken));

        var pending = await dbContext.TenantModuleDataPurges
            .AsNoTracking()
            .Where(p => p.TenantId == tenantId
                && p.Module == canonical
                && p.CancelledAt == null
                && p.ExecutedAt == null)
            .Select(p => new ModuleDataPurgePendingDto(p.Id, p.ScheduledFor))
            .FirstOrDefaultAsync(cancellationToken);

        return new ModuleDataPurgePreviewDto(canonical, moduleActive, tables, storageObjects, blockers, pending);
    }

    private async Task<bool> IsModuleActiveAsync(
        IModel model,
        TenantScopedSql sql,
        Guid tenantId,
        string module,
        CancellationToken cancellationToken)
    {
        var entity = model.FindEntityType(typeof(TenantModule))
            ?? throw new InvalidOperationException("TenantModule is not part of the model.");
        var tenantColumn = sql.Column(entity, Property(entity, nameof(TenantModule.TenantId)));
        var nameColumn = sql.Column(entity, Property(entity, nameof(TenantModule.ModuleName)));
        var activeColumn = sql.Column(entity, Property(entity, nameof(TenantModule.IsActive)));
        var rows = await ScalarAsync(
            sql,
            "COUNT(*)",
            $"{sql.Table(entity)} AS target WHERE target.{tenantColumn} = {{0}} "
                + $"AND target.{nameColumn} = {{1}} AND target.{activeColumn} = TRUE",
            cancellationToken,
            tenantId,
            module);
        return rows > 0;
    }

    /// <summary>
    /// Distinct storage keys on rows in scope: catalog product files, product request files and work
    /// order task files. No storage listing in v1; this only counts what the database points at.
    /// </summary>
    private async Task<long> CountStorageObjectsAsync(
        TenantScopedSql sql,
        IReadOnlyList<ModuleScopeEntry> plan,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var entry in plan.Where(e => ModuleDataPurgeStorage.FileTypes.Contains(e.Entity.ClrType)))
        {
            var key = sql.Column(entry.Entity, Property(entry.Entity, "StorageKey"));
            total += await ScalarAsync(
                sql,
                $"COUNT(DISTINCT target.{key})",
                $"{sql.Table(entry.Entity)} AS target WHERE {sql.Scope(entry.Entity, "target")}",
                cancellationToken,
                tenantId);
        }

        return total;
    }

    /// <summary>
    /// Rows outside the purge scope whose foreign key into the scope would make the database refuse
    /// the delete (anything but Cascade/SetNull: <c>fk.restrict</c>) or would silently delete them with
    /// the purge (a Cascade from outside the scope: <c>fk.cascade</c>). Borrowed planning rows count as
    /// in scope only for the rows the module owns.
    /// </summary>
    private async Task<IReadOnlyList<ModuleDataPurgeBlockerDto>> ForeignKeyBlockersAsync(
        IModel model,
        TenantScopedSql sql,
        IReadOnlyList<ModuleScopeEntry> plan,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var inPlan = plan.ToDictionary(entry => entry.Entity);
        var blockers = new List<ModuleDataPurgeBlockerDto>();
        foreach (var child in model.GetEntityTypes().OrderBy(entity => entity.Name, StringComparer.Ordinal))
        {
            inPlan.TryGetValue(child, out var childEntry);
            if (childEntry is not null && childEntry.RowFilter == PlanningRowFilter.None)
                continue;
            if (child.GetTableName() is null || !TenantScopedSql.HasTenantAncestor(child))
                continue;

            var restrictReferences = new List<string>();
            var cascadeReferences = new List<string>();
            foreach (var foreignKey in child.GetForeignKeys())
            {
                if (!inPlan.TryGetValue(foreignKey.PrincipalEntityType, out var principal))
                    continue;
                if (foreignKey.DeleteBehavior == DeleteBehavior.SetNull)
                    continue;

                var linked = string.Join(
                    " AND ",
                    foreignKey.Properties.Select(property => $"child.{sql.Column(child, property)} IS NOT NULL"));
                var principalFilter = FilterPredicate(sql, principal.Entity, principal.RowFilter, "pr");
                var reference = principalFilter is null
                    ? $"({linked})"
                    : $"({linked} AND EXISTS (SELECT 1 FROM {sql.Table(principal.Entity)} AS pr "
                        + $"WHERE {sql.Join(foreignKey, "child", "pr")} AND {principalFilter}))";
                if (foreignKey.DeleteBehavior == DeleteBehavior.Cascade)
                    cascadeReferences.Add(reference);
                else
                    restrictReferences.Add(reference);
            }

            foreach (var (code, references) in new[]
                     {
                         (ForeignKeyRestrictCode, restrictReferences),
                         (ForeignKeyCascadeCode, cascadeReferences),
                     })
            {
                if (references.Count == 0)
                    continue;

                var where = sql.Scope(child, "child");
                if (childEntry is not null)
                {
                    var own = FilterPredicate(sql, child, childEntry.RowFilter, "child");
                    if (own is not null)
                        where += $" AND NOT ({own})";
                }

                where += $" AND ({string.Join(" OR ", references)})";
                var rows = await ScalarAsync(
                    sql,
                    "COUNT(*)",
                    $"{sql.Table(child)} AS child WHERE {where}",
                    cancellationToken,
                    tenantId);
                if (rows > 0)
                    blockers.Add(new ModuleDataPurgeBlockerDto(code, QualifiedName(child), rows));
            }
        }

        return blockers;
    }

    /// <summary>
    /// Same rule as <c>AdminTenantService.EnsureNoUnreleasedAllocationsOnDisableAsync</c>, limited to
    /// the allocation tables in scope. For the planning module a never-started, manual, already
    /// expired allocation does not hold the purge either.
    /// </summary>
    private async Task<IReadOnlyList<ModuleDataPurgeBlockerDto>> UnreleasedAllocationBlockersAsync(
        TenantScopedSql sql,
        IReadOnlyList<ModuleScopeEntry> plan,
        string module,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var statuses = string.Join(", ", UnreleasedStatuses.Select(status => $"'{status}'"));
        var blockers = new List<ModuleDataPurgeBlockerDto>();
        foreach (var entry in plan.Where(e =>
                     e.Entity.ClrType == typeof(AssetAllocation)
                     || e.Entity.ClrType == typeof(CatalogOrderItemResourceAllocation)))
        {
            var entity = entry.Entity;
            var isPlanningRow = entity.ClrType == typeof(AssetAllocation);
            var statusColumn = sql.Column(entity, Property(entity, nameof(AssetAllocation.Status)));
            var where = $"{sql.Scope(entity, "target")} AND target.{statusColumn} IN ({statuses})";
            var filter = FilterPredicate(sql, entity, entry.RowFilter, "target");
            if (filter is not null)
                where += $" AND {filter}";

            var parameters = new List<object> { tenantId };
            if (isPlanningRow && module == PlatformModules.ResourcePlanning)
            {
                var source = sql.Column(entity, Property(entity, nameof(AssetAllocation.Source)));
                var reservation = sql.Column(entity, Property(entity, nameof(AssetAllocation.ReservationItemId)));
                var occupiedEnd = sql.Column(entity, Property(entity, nameof(AssetAllocation.OccupiedEnd)));
                where += $" AND NOT (target.{statusColumn} = '{AllocationStatus.Scheduled}'"
                    + $" AND target.{source} = '{AssetAllocationSource.Manual}'"
                    + $" AND target.{reservation} IS NULL AND target.{occupiedEnd} <= {{1}})";
                parameters.Add(DateTimeOffset.UtcNow);
            }

            var rows = await ScalarAsync(
                sql,
                "COUNT(*)",
                $"{sql.Table(entity)} AS target WHERE {where}",
                cancellationToken,
                parameters.ToArray());
            if (rows > 0)
                blockers.Add(new ModuleDataPurgeBlockerDto(UnreleasedAllocationsCode, QualifiedName(entity), rows));
        }

        return blockers;
    }

    /// <summary>
    /// SQL predicate (on <paramref name="alias"/>) selecting the planning rows a module owns;
    /// null when the module owns every row of the entity.
    /// </summary>
    internal static string? FilterPredicate(
        TenantScopedSql sql,
        IEntityType entity,
        PlanningRowFilter filter,
        string alias)
    {
        switch (filter)
        {
            case PlanningRowFilter.CatalogOrderSource:
                return $"{alias}.{sql.Column(entity, Property(entity, nameof(AssetAllocation.Source)))} = "
                    + $"'{AssetAllocationSource.CatalogOrder}'";
            case PlanningRowFilter.WorkOrderSource:
                return $"{alias}.{sql.Column(entity, Property(entity, nameof(AssetAllocation.Source)))} = "
                    + $"'{AssetAllocationSource.WorkOrder}'";
            case PlanningRowFilter.ReservationBridge:
                return $"{alias}.{sql.Column(entity, Property(entity, nameof(AssetAllocation.ReservationItemId)))} IS NOT NULL";
            default:
                return null;
        }
    }

    internal static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name)
        ?? throw new InvalidOperationException($"{entity.Name} has no property {name}.");

    internal static string QualifiedName(IEntityType entity) =>
        entity.GetSchema() is { } schema
            ? $"{schema}.{entity.GetTableName()}"
            : entity.GetTableName()!;

    /// <summary>
    /// Runs <c>SELECT {expression} FROM {fromAndWhere}</c>. Both strings are built from
    /// schema-derived identifiers, constants and numbered placeholders only; every value (tenant id,
    /// module key, timestamps) travels as a parameter.
    /// </summary>
    private async Task<long> ScalarAsync(
        TenantScopedSql sql,
        string expression,
        string fromAndWhere,
        CancellationToken cancellationToken,
        params object[] parameters)
    {
        // Interpolated segments are schema-derived identifiers, not user input (same as TenantPurger).
#pragma warning disable EF1002
        return await dbContext.Database
            .SqlQueryRaw<long>($"SELECT {expression} AS {sql.Delimit("Value")} FROM {fromAndWhere}", parameters)
            .SingleAsync(cancellationToken);
#pragma warning restore EF1002
    }
}

/// <summary>The file entities whose <c>StorageKey</c> points at a storage object (spec 3.2).</summary>
internal static class ModuleDataPurgeStorage
{
    internal static readonly Type[] FileTypes =
        [typeof(CatalogProductFile), typeof(ProductRequestFile), typeof(WorkOrderTaskFile)];
}
