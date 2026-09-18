using Microsoft.EntityFrameworkCore;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Core.Domain.Entities;
using Platform.Core.Domain.Enums;
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
            .Where(template => template.Status == GlobalTemplateStatus.Published)
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

    public async Task<GlobalMaintenanceTemplateResponse?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken)
    {
        var template = await dbContext.GlobalMaintenanceTemplates
            .AsNoTracking()
            .Include(item => item.Tasks)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        return template is null ? null : ToResponse(template);
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
            template.LibraryKey,
            template.Version,
            template.Status,
            template.SourceReferences,
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
