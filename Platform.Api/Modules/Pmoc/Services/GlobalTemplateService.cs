using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Pmoc.Services;

public sealed class GlobalTemplateService(
    AppDbContext dbContext) : IGlobalTemplateService
{
    public async Task<IReadOnlyList<GlobalMaintenanceTemplateResponse>> ListAsync(
        string? jurisdiction,
        CancellationToken cancellationToken)
    {
        var query = dbContext.GlobalMaintenanceTemplates
            .AsNoTracking()
            .Include(template => template.Tasks)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(jurisdiction))
        {
            var normalized = jurisdiction.Trim().ToUpperInvariant();

            // Always include national "BR" templates plus the requested jurisdiction.
            query = query.Where(template =>
                template.Jurisdiction == "BR"
                || template.Jurisdiction.ToUpper() == normalized);
        }

        var templates = await query
            .OrderBy(template => template.Name)
            .ToListAsync(cancellationToken);

        return templates.Select(ToResponse).ToList();
    }

    private static GlobalMaintenanceTemplateResponse ToResponse(
        GlobalMaintenanceTemplate template) =>
        new(
            template.Id,
            template.Name,
            template.Description,
            template.Frequency,
            template.Jurisdiction,
            template.TargetEquipmentType,
            template.Tasks
                .OrderBy(task => task.Order)
                .Select(ToTaskResponse)
                .ToList(),
            template.CreatedAt,
            template.UpdatedAt);

    private static GlobalTemplateTaskResponse ToTaskResponse(GlobalTemplateTask task) =>
        new(
            task.Id,
            task.GlobalMaintenanceTemplateId,
            task.Title,
            task.InputType,
            task.Configuration,
            task.IsMandatory,
            task.Order,
            task.CreatedAt,
            task.UpdatedAt);
}
