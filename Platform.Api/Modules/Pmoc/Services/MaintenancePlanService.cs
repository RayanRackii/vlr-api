using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Pmoc.Services;

public sealed class MaintenancePlanService(
    AppDbContext dbContext,
    ITenantProvider tenantProvider) : IMaintenancePlanService
{
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
        ValidateTasks(request.Tasks);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var plan = new MaintenancePlan
            {
                TenantId = tenantId,
                UnitId = request.UnitId,
                Name = request.Name.Trim(),
                Description = NormalizeOptional(request.Description),
                Frequency = request.Frequency,
                AssetCategoryId = request.AssetCategoryId,
                IsActive = request.IsActive,
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

        plan.UnitId = request.UnitId;
        plan.Name = request.Name.Trim();
        plan.Description = NormalizeOptional(request.Description);
        plan.Frequency = request.Frequency;
        plan.AssetCategoryId = request.AssetCategoryId;
        plan.IsActive = request.IsActive;
        plan.Touch();

        await dbContext.SaveChangesAsync(cancellationToken);

        return ToResponse(plan);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        EnsureTenantContext();

        var plan = await dbContext.MaintenancePlans
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (plan is null)
        {
            return false;
        }

        dbContext.MaintenancePlans.Remove(plan);
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
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
        var exists = await dbContext.AssetCategories
            .AsNoTracking()
            .AnyAsync(c => c.Id == assetCategoryId, cancellationToken);

        if (!exists)
        {
            throw new KeyNotFoundException($"Asset category '{assetCategoryId}' was not found.");
        }
    }

    private static void ValidateTasks(IReadOnlyList<CreatePlanTaskDto> tasks)
    {
        if (tasks.Count == 0)
        {
            throw new ArgumentException("At least one plan task is required.");
        }

        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Title))
            {
                throw new ArgumentException("Plan task title is required.");
            }

            if (!string.IsNullOrWhiteSpace(task.Configuration))
            {
                try
                {
                    using var _ = JsonDocument.Parse(task.Configuration);
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException(
                        $"Plan task '{task.Title}' has invalid Configuration JSON.",
                        ex);
                }
            }
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
            plan.Frequency,
            plan.AssetCategoryId,
            plan.IsActive,
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
