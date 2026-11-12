using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class MaintenancePlan : Entity, ITenantScoped, IMaintenanceModuleEntity
{
    public required Guid TenantId { get; set; }

    public required Guid UnitId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required int IntervalDays { get; set; }

    public required DateOnly FirstDueDate { get; set; }

    /// <summary>Type target. Exactly one of this and <see cref="AssetId"/> is set. Existing plans stay type-scoped.</summary>
    public Guid? AssetCategoryId { get; set; }

    /// <summary>Specific asset target. When set, this plan wins over a type plan for that asset.</summary>
    public Guid? AssetId { get; set; }

    public required bool IsActive { get; set; }

    public MaintenancePlanOriginKind OriginKind { get; set; } = MaintenancePlanOriginKind.Custom;

    public Guid? SourceTemplateId { get; set; }

    public int? SourceTemplateVersion { get; set; }

    public bool AutoGenerateEnabled { get; set; }

    public Unit Unit { get; set; } = null!;

    public AssetCategory? AssetCategory { get; set; }

    public Asset? Asset { get; set; }

    private readonly List<PlanTask> _tasks = [];

    public IReadOnlyCollection<PlanTask> Tasks => _tasks.AsReadOnly();

    public void AddTask(PlanTask task)
    {
        _tasks.Add(task);
    }

    public void RemoveTask(PlanTask task)
    {
        _tasks.Remove(task);
    }
}
