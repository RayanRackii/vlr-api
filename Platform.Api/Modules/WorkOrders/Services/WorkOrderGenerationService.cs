using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Authorization;
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

        if (asset.Status != AssetStatus.Active
            || asset.ScheduledDeletionAt is not null
            || asset.UnitId != plan.UnitId
            || asset.CategoryId != plan.AssetCategoryId)
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

            var workOrder = new WorkOrder
            {
                TenantId = plan.TenantId,
                AssetId = asset.Id,
                MaintenancePlanId = plan.Id,
                AssignedUserId = assignedUser?.Id,
                Status = WorkOrderStatus.Pending,
                ScheduledDate = command.ScheduledDate,
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
