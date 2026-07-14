using Microsoft.EntityFrameworkCore;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class PmocEngineJob(
    AppDbContext dbContext,
    ILogger<PmocEngineJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        logger.LogInformation("PmocEngineJob started for {ScheduledDate}.", today);

        var activePlans = await dbContext.MaintenancePlans
            .Include(plan => plan.Tasks)
            .Where(plan => plan.IsActive)
            .OrderBy(plan => plan.Name)
            .ToListAsync(cancellationToken);

        logger.LogInformation(
            "PmocEngineJob found {Count} active maintenance plan(s).",
            activePlans.Count);

        var createdCount = 0;

        foreach (var plan in activePlans)
        {
            if (!IsDueToday(plan.Frequency, today))
            {
                logger.LogInformation(
                    "Skipping PMOC {PlanName}: frequency {Frequency} is not due on {ScheduledDate}.",
                    plan.Name,
                    plan.Frequency,
                    today);
                continue;
            }

            if (plan.Tasks.Count == 0)
            {
                logger.LogWarning(
                    "Skipping PMOC {PlanName}: plan has no tasks.",
                    plan.Name);
                continue;
            }

            logger.LogInformation("Verificando PMOC: {PlanName}", plan.Name);

            var assets = await dbContext.Assets
                .Where(asset =>
                    asset.CategoryId == plan.AssetCategoryId
                    && asset.UnitId == plan.UnitId
                    && asset.Status == AssetStatus.Active
                    && asset.ScheduledDeletionAt == null)
                .ToListAsync(cancellationToken);

            if (assets.Count == 0)
            {
                logger.LogInformation(
                    "PMOC {PlanName}: no eligible assets found for unit/category.",
                    plan.Name);
                continue;
            }

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

            try
            {
                var planCreated = 0;

                foreach (var asset in assets)
                {
                    var alreadyExists = await dbContext.WorkOrders.AnyAsync(
                        workOrder =>
                            workOrder.AssetId == asset.Id
                            && workOrder.MaintenancePlanId == plan.Id
                            && workOrder.ScheduledDate == today
                            && workOrder.Status != WorkOrderStatus.Canceled,
                        cancellationToken);

                    if (alreadyExists)
                    {
                        continue;
                    }

                    var workOrder = new WorkOrder
                    {
                        TenantId = plan.TenantId,
                        AssetId = asset.Id,
                        MaintenancePlanId = plan.Id,
                        Status = WorkOrderStatus.Pending,
                        ScheduledDate = today,
                        Notes = null,
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
                    planCreated++;
                }

                if (planCreated > 0)
                {
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);

                createdCount += planCreated;

                logger.LogInformation(
                    "PMOC {PlanName}: created {CreatedCount} work order(s) for {AssetCount} asset(s).",
                    plan.Name,
                    planCreated,
                    assets.Count);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(cancellationToken);
                logger.LogError(
                    ex,
                    "PMOC {PlanName}: failed while generating work orders.",
                    plan.Name);
            }
        }

        logger.LogInformation(
            "PmocEngineJob completed. Created {CreatedCount} work order(s).",
            createdCount);
    }

    private static bool IsDueToday(MaintenanceFrequency frequency, DateOnly today) =>
        frequency switch
        {
            MaintenanceFrequency.Daily => true,
            MaintenanceFrequency.Weekly => today.DayOfWeek == DayOfWeek.Monday,
            MaintenanceFrequency.Monthly => today.Day == 1,
            MaintenanceFrequency.Quarterly =>
                today.Day == 1 && today.Month is 1 or 4 or 7 or 10,
            MaintenanceFrequency.Semiannual =>
                today.Day == 1 && today.Month is 1 or 7,
            MaintenanceFrequency.Annual =>
                today.Day == 1 && today.Month == 1,
            _ => false,
        };
}
