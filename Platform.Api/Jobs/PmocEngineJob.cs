using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.WorkOrders;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Jobs;

public sealed class PmocEngineJob(
    AppDbContext dbContext,
    IWorkOrderGenerationService workOrderGenerationService,
    ILogger<PmocEngineJob> logger)
{
    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var today = HangfireExtensions.GetBrazilToday();

        logger.LogInformation(
            "Iniciando geração de OS para o dia {ScheduledDate} (fuso Brasil).",
            today);

        try
        {
            var activePlans = await dbContext.MaintenancePlans
                .Include(plan => plan.Tasks)
                .Where(plan => plan.IsActive && plan.AutoGenerateEnabled)
                .OrderBy(plan => plan.Name)
                .ToListAsync(cancellationToken);

            logger.LogInformation(
                "PmocEngineJob found {Count} active maintenance plan(s).",
                activePlans.Count);

            var createdCount = 0;

            foreach (var plan in activePlans)
            {
                try
                {
                    if (!PmocDueCalendar.IsDueToday(plan.Frequency, today))
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

                    var planCreated = 0;

                    foreach (var asset in assets)
                    {
                        try
                        {
                            await workOrderGenerationService.GenerateAsync(
                                new GenerateWorkOrderCommand(
                                    plan.Id,
                                    asset.Id,
                                    today,
                                    AssignedUserId: null),
                                cancellationToken);
                            planCreated++;
                        }
                        catch (DuplicateWorkOrderException)
                        {
                        }
                        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
                        {
                            logger.LogWarning(
                                ex,
                                "PMOC {PlanName}: skipped asset {AssetId} while generating work orders.",
                                plan.Name,
                                asset.Id);
                        }
                    }

                    createdCount += planCreated;

                    logger.LogInformation(
                        "PMOC {PlanName}: created {CreatedCount} work order(s) for {AssetCount} asset(s).",
                        plan.Name,
                        planCreated,
                        assets.Count);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex,
                        "PMOC {PlanName}: failed while generating work orders.",
                        plan.Name);
                }
            }

            logger.LogInformation(
                "PmocEngineJob completed. Created {CreatedCount} work order(s) for {ScheduledDate}.",
                createdCount,
                today);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "PmocEngineJob failed while generating work orders for {ScheduledDate}.",
                today);
            throw;
        }
    }
}
