using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class MaintenancePlan : Entity, ITenantScoped, IMaintenanceModuleEntity
{
    public required Guid TenantId { get; set; }

    public required Guid UnitId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required MaintenanceFrequency Frequency { get; set; }

    public required Guid AssetCategoryId { get; set; }

    public required bool IsActive { get; set; }

    public Unit Unit { get; set; } = null!;

    public AssetCategory AssetCategory { get; set; } = null!;

    private readonly List<PlanTask> _tasks = [];

    public IReadOnlyCollection<PlanTask> Tasks => _tasks.AsReadOnly();

    public void AddTask(PlanTask task)
    {
        _tasks.Add(task);
    }
}
