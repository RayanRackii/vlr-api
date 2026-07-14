using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Catalog template shared across all tenants (no tenant isolation).
/// </summary>
public class GlobalMaintenanceTemplate : Entity
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public required MaintenanceFrequency Frequency { get; set; }

    /// <summary>
    /// Jurisdiction code, e.g. "BR", "PR", "SP".
    /// </summary>
    public required string Jurisdiction { get; set; }

    public required string TargetEquipmentType { get; set; }

    private readonly List<GlobalTemplateTask> _tasks = [];

    public IReadOnlyCollection<GlobalTemplateTask> Tasks => _tasks.AsReadOnly();

    public void AddTask(GlobalTemplateTask task)
    {
        _tasks.Add(task);
    }
}
