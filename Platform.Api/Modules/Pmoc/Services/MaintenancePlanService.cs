using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Jobs;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Pmoc.Services;

public sealed class MaintenancePlanService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider,
    IAssetRegistry assetRegistry) : IMaintenancePlanService
{
    internal const string WorkOrderPlanForeignKeyName =
        "fk_work_orders_maintenance_plans_maintenance_plan_id";

    public async Task<IReadOnlyList<MaintenancePlanResponse>> ListAsync(
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plans = await dbContext.MaintenancePlans
            .AsNoTracking()
            .Include(p => p.Tasks)
            .OrderBy(p => p.Name)
            .ToListAsync(cancellationToken);

        return plans.Select(ToResponse).ToList();
    }

    public async Task<MaintenancePlanResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .AsNoTracking()
            .Include(p => p.Tasks)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        return plan is null ? null : ToResponse(plan);
    }

    public async Task<MaintenancePlanResponse> CreatePlanWithTasksAsync(
        CreateMaintenancePlanRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureAssetCategoryExistsAsync(request.AssetCategoryId, cancellationToken);
        ValidateCreateTasks(request.Tasks);
        PmocDueCalculator.EnsureIntervalDays(request.IntervalDays);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var plan = new MaintenancePlan
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                Name = request.Name.Trim(),
                Description = NormalizeOptional(request.Description),
                IntervalDays = request.IntervalDays,
                FirstDueDate = request.FirstDueDate,
                AssetCategoryId = request.AssetCategoryId,
                IsActive = request.IsActive,
                OriginKind = MaintenancePlanOriginKind.Custom,
                AutoGenerateEnabled = request.AutoGenerateEnabled,
            };

            foreach (var taskDto in request.Tasks.OrderBy(t => t.Order))
            {
                plan.AddTask(new PlanTask
                {
                    TenantId = tenantId,
                    MaintenancePlanId = plan.Id,
                    Title = taskDto.Title.Trim(),
                    InputType = taskDto.InputType,
                    IsMandatory = taskDto.IsMandatory,
                    Order = taskDto.Order,
                    Configuration = NormalizeConfiguration(taskDto.Configuration),
                });
            }

            dbContext.MaintenancePlans.Add(plan);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponse(plan);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MaintenancePlanResponse> CreateFromTemplateAsync(
        CreateFromTemplateRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var template = await dbContext.GlobalMaintenanceTemplates
            .Include(item => item.Tasks)
            .FirstOrDefaultAsync(item => item.Id == request.TemplateId, cancellationToken);

        if (template is null)
        {
            throw new KeyNotFoundException($"Template '{request.TemplateId}' was not found.");
        }

        if (template.Status != GlobalTemplateStatus.Published)
        {
            throw new ArgumentException("Only published templates can be cloned.");
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureAssetCategoryExistsAsync(request.AssetCategoryId, cancellationToken);

        var taskDtos = template.Tasks
            .OrderBy(task => task.Order)
            .Select(task => new CreatePlanTaskDto
            {
                Title = task.Title,
                InputType = task.InputType,
                IsMandatory = task.IsMandatory,
                Order = task.Order,
                Configuration = task.Configuration,
            })
            .ToList();

        ValidateCreateTasks(taskDtos);
        PmocDueCalculator.EnsureIntervalDays(request.IntervalDays);

        var name = string.IsNullOrWhiteSpace(request.Name)
            ? template.Name
            : request.Name.Trim();
        var description = request.Description is null
            ? template.Description
            : NormalizeOptional(request.Description);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var plan = new MaintenancePlan
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                Name = name,
                Description = description,
                IntervalDays = request.IntervalDays,
                FirstDueDate = request.FirstDueDate,
                AssetCategoryId = request.AssetCategoryId,
                IsActive = request.IsActive,
                OriginKind = MaintenancePlanOriginKind.RolvixTemplate,
                SourceTemplateId = template.Id,
                SourceTemplateVersion = template.Version,
                AutoGenerateEnabled = request.AutoGenerateEnabled,
            };

            foreach (var taskDto in taskDtos)
            {
                plan.AddTask(new PlanTask
                {
                    TenantId = tenantId,
                    MaintenancePlanId = plan.Id,
                    Title = taskDto.Title.Trim(),
                    InputType = taskDto.InputType,
                    IsMandatory = taskDto.IsMandatory,
                    Order = taskDto.Order,
                    Configuration = NormalizeConfiguration(taskDto.Configuration),
                });
            }

            dbContext.MaintenancePlans.Add(plan);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponse(plan);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MaintenancePlanResponse?> UpdateAsync(
        Guid id,
        UpdateMaintenancePlanRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .Include(p => p.Tasks)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (plan is null)
        {
            return null;
        }

        await EnsureUnitExistsAsync(request.UnitId, cancellationToken);
        await EnsureAssetCategoryExistsAsync(request.AssetCategoryId, cancellationToken);
        PmocDueCalculator.EnsureIntervalDays(request.IntervalDays);

        plan.UnitId = request.UnitId;
        plan.Name = request.Name.Trim();
        plan.Description = NormalizeOptional(request.Description);
        plan.IntervalDays = request.IntervalDays;
        plan.FirstDueDate = request.FirstDueDate;
        plan.AssetCategoryId = request.AssetCategoryId;
        plan.IsActive = request.IsActive;
        plan.AutoGenerateEnabled = request.AutoGenerateEnabled;
        plan.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(plan);
    }

    public async Task<MaintenancePlanResponse?> ReplaceTasksAsync(
        Guid id,
        ReplacePlanTasksRequest request,
        CancellationToken cancellationToken)
    {
        var tenantId = EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .Include(p => p.Tasks)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (plan is null)
        {
            return null;
        }

        ValidateReplaceTasks(request.Tasks);

        var incomingIds = request.Tasks
            .Where(task => task.Id is Guid)
            .Select(task => task.Id!.Value)
            .ToList();

        if (incomingIds.Count != incomingIds.Distinct().Count())
        {
            throw new ArgumentException("Duplicate plan task ids are not allowed.");
        }

        var existingById = plan.Tasks.ToDictionary(task => task.Id);
        foreach (var taskId in incomingIds)
        {
            if (!existingById.ContainsKey(taskId))
            {
                throw new KeyNotFoundException($"Plan task '{taskId}' was not found.");
            }
        }

        var removed = plan.Tasks
            .Where(task => !incomingIds.Contains(task.Id))
            .ToList();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (removed.Count > 0)
            {
                var removedIds = removed.Select(task => task.Id).ToList();
                var linkedWorkOrderTasks = await dbContext.WorkOrderTasks
                    .Where(task => task.PlanTaskId != null && removedIds.Contains(task.PlanTaskId.Value))
                    .ToListAsync(cancellationToken);

                foreach (var workOrderTask in linkedWorkOrderTasks)
                {
                    workOrderTask.PlanTaskId = null;
                }

                foreach (var task in removed)
                {
                    plan.RemoveTask(task);
                    dbContext.PlanTasks.Remove(task);
                }
            }

            foreach (var dto in request.Tasks.OrderBy(task => task.Order))
            {
                if (dto.Id is Guid taskId)
                {
                    var existing = existingById[taskId];
                    existing.Title = dto.Title.Trim();
                    existing.InputType = dto.InputType;
                    existing.IsMandatory = dto.IsMandatory;
                    existing.Order = dto.Order;
                    existing.Configuration = NormalizeConfiguration(dto.Configuration);
                    existing.Touch();
                    continue;
                }

                plan.AddTask(new PlanTask
                {
                    TenantId = tenantId,
                    MaintenancePlanId = plan.Id,
                    Title = dto.Title.Trim(),
                    InputType = dto.InputType,
                    IsMandatory = dto.IsMandatory,
                    Order = dto.Order,
                    Configuration = NormalizeConfiguration(dto.Configuration),
                });
            }

            plan.Touch();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponse(plan);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .Include(p => p.Tasks)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (plan is null)
        {
            return false;
        }

        var inUse = await dbContext.WorkOrders
            .AnyAsync(workOrder => workOrder.MaintenancePlanId == plan.Id, cancellationToken);

        if (inUse)
        {
            throw new PlanInUseException();
        }

        try
        {
            dbContext.MaintenancePlans.Remove(plan);
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsWorkOrderPlanRestrictViolation(ex))
        {
            throw new PlanInUseException();
        }
    }

    internal static bool IsWorkOrderPlanRestrictViolation(DbUpdateException exception)
    {
        for (var current = exception as Exception; current is not null; current = current.InnerException)
        {
            if (current is not PostgresException postgres
                || postgres.SqlState != PostgresErrorCodes.ForeignKeyViolation)
            {
                continue;
            }

            var blob = string.Join(
                ' ',
                postgres.ConstraintName,
                postgres.TableName,
                postgres.Detail,
                postgres.MessageText);

            if (blob.Contains(WorkOrderPlanForeignKeyName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task EnsureUnitExistsAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var exists = await dbContext.Units
            .AsNoTracking()
            .AnyAsync(u => u.Id == unitId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Unit '{unitId}' was not found.");
        }
    }

    private async Task EnsureAssetCategoryExistsAsync(
        Guid assetCategoryId,
        CancellationToken cancellationToken)
    {
        await assetRegistry.RequireCategoryAsync(assetCategoryId, cancellationToken);
    }

    private static void ValidateCreateTasks(IReadOnlyList<CreatePlanTaskDto> tasks)
    {
        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one plan task is required.");
        }

        foreach (var task in tasks)
        {
            ValidateTaskFields(task.Title, task.Configuration);
        }
    }

    private static void ValidateReplaceTasks(IReadOnlyList<ReplacePlanTaskDto>? tasks)
    {
        if (tasks is null || tasks.Count == 0)
        {
            throw new ArgumentException("At least one plan task is required.");
        }

        foreach (var task in tasks)
        {
            ValidateTaskFields(task.Title, task.Configuration);
        }
    }

    private static void ValidateTaskFields(string title, string? configuration)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Plan task title is required.");
        }

        if (string.IsNullOrWhiteSpace(configuration))
        {
            return;
        }

        try
        {
            using var _ = JsonDocument.Parse(configuration);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException(
                $"Plan task '{title}' has invalid Configuration JSON.",
                ex);
        }
    }

    private Guid EnsureTenantContext()
    {
        return tenantProvider.TenantId
            ?? throw new UnauthorizedAccessException("Tenant context is required.");
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static string? NormalizeConfiguration(string? configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration))
        {
            return null;
        }

        return configuration.Trim();
    }

    private static MaintenancePlanResponse ToResponse(MaintenancePlan plan) =>
        new(
            plan.Id,
            plan.TenantId,
            plan.UnitId,
            plan.Name,
            plan.Description,
            plan.IntervalDays,
            plan.FirstDueDate,
            plan.AssetCategoryId,
            plan.IsActive,
            plan.OriginKind,
            plan.SourceTemplateId,
            plan.SourceTemplateVersion,
            plan.AutoGenerateEnabled,
            plan.Tasks
                .OrderBy(t => t.Order)
                .Select(ToTaskResponse)
                .ToList(),
            plan.CreatedAt,
            plan.UpdatedAt);

    private static PlanTaskResponse ToTaskResponse(PlanTask task) =>
        new(
            task.Id,
            task.TenantId,
            task.MaintenancePlanId,
            task.Title,
            task.InputType,
            task.IsMandatory,
            task.Order,
            task.Configuration,
            task.CreatedAt,
            task.UpdatedAt);
}
