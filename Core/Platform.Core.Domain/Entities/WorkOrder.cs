using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class WorkOrder : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid AssetId { get; set; }

    public Guid? MaintenancePlanId { get; set; }

    public required WorkOrderStatus Status { get; set; }

    public required DateOnly ScheduledDate { get; set; }

    public DateTimeOffset? CompletedDate { get; set; }

    public string? Notes { get; set; }

    public Asset Asset { get; set; } = null!;

    public MaintenancePlan? MaintenancePlan { get; set; }

    private readonly List<WorkOrderTask> _tasks = [];

    public IReadOnlyCollection<WorkOrderTask> Tasks => _tasks.AsReadOnly();

    public void AddTask(WorkOrderTask task)
    {
        _tasks.Add(task);
    }
}
