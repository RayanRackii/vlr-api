using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class WorkOrderTask : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid WorkOrderId { get; set; }

    public Guid? PlanTaskId { get; set; }

    public required string Title { get; set; }

    public required TaskInputType InputType { get; set; }

    /// <summary>
    /// Serialized JSON copied from the originating PlanTask configuration.
    /// </summary>
    public string? Configuration { get; set; }

    public required bool IsMandatory { get; set; }

    public required int Order { get; set; }

    /// <summary>
    /// Technician response. Text, number, choice, or the id of a <see cref="WorkOrderTaskFile"/>.
    /// </summary>
    public string? Value { get; set; }

    public WorkOrder WorkOrder { get; set; } = null!;

    public PlanTask? PlanTask { get; set; }
}
