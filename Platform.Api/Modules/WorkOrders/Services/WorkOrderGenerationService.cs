using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Authorization;
using Platform.Api.Jobs;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.WorkOrders.Services;

public sealed class WorkOrderGenerationService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IPermissionResolver permissionResolver) : IWorkOrderGenerationService
{
    internal const string PmocPeriodUniqueIndexName = "ux_os_work_orders_pmoc_period";

    public async Task<WorkOrderResponse> GenerateAsync(
        GenerateWorkOrderCommand command,
        CancellationToken cancellationToken)
    {
        var plan = await dbContext.MaintenancePlans
            .Include(item => item.Tasks)
            .FirstOrDefaultAsync(item => item.Id == command.PlanId, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"Maintenance plan '{command.PlanId}' was not found.");

        if (!plan.IsActive)
        {
            throw new ArgumentException("Maintenance plan is not active.");
        }

        if (plan.Tasks.Count == 0)
        {
            throw new ArgumentException("At least one plan task is required.");
        }

        var asset = await dbContext.Assets
            .FirstOrDefaultAsync(item => item.Id == command.AssetId, cancellationToken)
            ?? throw new KeyNotFoundException($"Asset '{command.AssetId}' was not found.");

        if (!PmocAssetEligibility.Matches(asset, plan.TenantId, plan.UnitId, plan.AssetCategoryId))
        {
            throw new ArgumentException(
                $"Asset '{asset.Id}' is not eligible for this maintenance plan.");
        }

        var permissionTenantId = tenantProvider.TenantId ?? plan.TenantId;
        var assignedUser = await WorkOrderAssigneeGuard.ResolveOptionalAsync(
            dbContext,
            permissionResolver,
            permissionTenantId,
            command.AssignedUserId,
            cancellationToken);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await PmocPlanAssetLock.AcquireAsync(
                dbContext,
                plan.Id,
                asset.Id,
                cancellationToken);

            var duplicate = await dbContext.WorkOrders.AnyAsync(
                workOrder =>
                    workOrder.TenantId == plan.TenantId
                    && workOrder.AssetId == asset.Id
                    && workOrder.MaintenancePlanId == plan.Id
                    && workOrder.ScheduledDate == command.ScheduledDate
                    && workOrder.Status != WorkOrderStatus.Canceled,
                cancellationToken);

            if (duplicate)
            {
                throw new DuplicateWorkOrderException();
            }

            var workOrder = await InsertSnapshotAsync(
                plan,
                asset,
                command.ScheduledDate,
                assignedUser?.Id,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            workOrder.Asset = asset;
            workOrder.AssignedUser = assignedUser;
            return WorkOrderResponseMapper.ToResponse(workOrder);
        }
        catch (DbUpdateException ex) when (IsPmocPeriodUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            DiscardFailedGeneration();
            throw new DuplicateWorkOrderException();
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DiscardFailedGeneration();
            throw;
        }
    }

    public async Task<PmocAutomaticGenerationResult> TryGenerateAutomaticAsync(
        Guid planId,
        Guid assetId,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            await PmocPlanAssetLock.AcquireAsync(dbContext, planId, assetId, cancellationToken);
            await PmocPlanAssetLock.LockPlanRowAsync(dbContext, planId, cancellationToken);

            var plan = await dbContext.MaintenancePlans
                .IgnoreQueryFilters()
                .Include(item => item.Tasks)
                .FirstOrDefaultAsync(item => item.Id == planId, cancellationToken);

            if (plan is null)
            {
                return SkippedRevalidation();
            }

            await dbContext.Entry(plan).ReloadAsync(cancellationToken);
            await dbContext.Entry(plan).Collection(item => item.Tasks).LoadAsync(cancellationToken);

            if (!plan.IsActive || !plan.AutoGenerateEnabled || plan.Tasks.Count == 0)
            {
                return SkippedRevalidation();
            }

            await PmocPlanAssetLock.LockAssetRowAsync(dbContext, assetId, cancellationToken);

            var asset = await dbContext.Assets
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(item => item.Id == assetId, cancellationToken);

            if (asset is null
                || !PmocAssetEligibility.Matches(asset, plan.TenantId, plan.UnitId, plan.AssetCategoryId))
            {
                return SkippedRevalidation();
            }

            await dbContext.Entry(asset).ReloadAsync(cancellationToken);

            if (!PmocAssetEligibility.Matches(asset, plan.TenantId, plan.UnitId, plan.AssetCategoryId))
            {
                return SkippedRevalidation();
            }

            var relevant = await dbContext.WorkOrders
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(workOrder =>
                    workOrder.TenantId == plan.TenantId
                    && workOrder.MaintenancePlanId == plan.Id
                    && workOrder.AssetId == asset.Id
                    && workOrder.Status != WorkOrderStatus.Canceled)
                .Select(workOrder => new
                {
                    workOrder.Id,
                    workOrder.Status,
                    workOrder.ScheduledDate,
                    workOrder.CompletedDate,
                })
                .ToListAsync(cancellationToken);

            var lastCompleted = PmocDueCalculator.PickLastCompleted(
                relevant.Select(workOrder => (
                    workOrder.Id,
                    workOrder.Status,
                    workOrder.ScheduledDate,
                    workOrder.CompletedDate)));

            var due = PmocDueCalculator.Compute(
                new PmocDueInput(
                    plan.IntervalDays,
                    plan.FirstDueDate,
                    asOfDate,
                    lastCompleted));

            if (due.DueStatus == PmocDueStatus.NotDue)
            {
                return new PmocAutomaticGenerationResult(
                    PmocAutomaticGenerationOutcome.SkippedNotDue,
                    null,
                    due.NextDueDate);
            }

            if (relevant.Any(workOrder =>
                    workOrder.Status is WorkOrderStatus.Pending or WorkOrderStatus.InProgress))
            {
                return new PmocAutomaticGenerationResult(
                    PmocAutomaticGenerationOutcome.SkippedOpenWorkOrder,
                    null,
                    due.NextDueDate);
            }

            if (relevant.Any(workOrder => workOrder.ScheduledDate == due.NextDueDate))
            {
                return new PmocAutomaticGenerationResult(
                    PmocAutomaticGenerationOutcome.SkippedDuplicate,
                    null,
                    due.NextDueDate);
            }

            var workOrder = await InsertSnapshotAsync(
                plan,
                asset,
                due.NextDueDate,
                assignedUserId: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new PmocAutomaticGenerationResult(
                PmocAutomaticGenerationOutcome.Created,
                workOrder.Id,
                due.NextDueDate);
        }
        catch (DbUpdateException ex) when (IsPmocPeriodUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);
            DiscardFailedGeneration();
            return new PmocAutomaticGenerationResult(
                PmocAutomaticGenerationOutcome.SkippedDuplicate,
                null,
                null);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            DiscardFailedGeneration();
            throw;
        }
    }

    private async Task<WorkOrder> InsertSnapshotAsync(
        MaintenancePlan plan,
        Asset asset,
        DateOnly scheduledDate,
        Guid? assignedUserId,
        CancellationToken cancellationToken)
    {
        var workOrder = new WorkOrder
        {
            TenantId = plan.TenantId,
            AssetId = asset.Id,
            MaintenancePlanId = plan.Id,
            AssignedUserId = assignedUserId,
            Status = WorkOrderStatus.Pending,
            ScheduledDate = scheduledDate,
            Notes = null,
            SourcePlanName = plan.Name,
        };

        foreach (var planTask in plan.Tasks.OrderBy(task => task.Order))
        {
            workOrder.AddTask(new WorkOrderTask
            {
                TenantId = plan.TenantId,
                WorkOrderId = workOrder.Id,
                PlanTaskId = planTask.Id,
                Title = planTask.Title,
                InputType = planTask.InputType,
                Configuration = planTask.Configuration,
                IsMandatory = planTask.IsMandatory,
                Order = planTask.Order,
                Value = null,
            });
        }

        dbContext.WorkOrders.Add(workOrder);
        await dbContext.SaveChangesAsync(cancellationToken);
        return workOrder;
    }

    private static PmocAutomaticGenerationResult SkippedRevalidation() =>
        new(PmocAutomaticGenerationOutcome.SkippedRevalidation, null, null);

    internal void DiscardFailedGeneration()
    {
        dbContext.ChangeTracker.Clear();
    }

    internal static bool IsPmocPeriodUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception as Exception; current is not null; current = current.InnerException)
        {
            if (current is not PostgresException postgres
                || postgres.SqlState != PostgresErrorCodes.UniqueViolation)
            {
                continue;
            }

            var blob = string.Join(
                ' ',
                postgres.ConstraintName,
                postgres.TableName,
                postgres.Detail,
                postgres.MessageText);

            if (blob.Contains(PmocPeriodUniqueIndexName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
