using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class PlanTask : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid MaintenancePlanId { get; set; }

    public required string Title { get; set; }

    public required TaskInputType InputType { get; set; }

    public required bool IsMandatory { get; set; }

    public required int Order { get; set; }

    /// <summary>
    /// Serialized JSON for dynamic field configuration
    /// (e.g. min/max for Number, options for SingleChoice).
    /// </summary>
    public string? Configuration { get; set; }

    public MaintenancePlan MaintenancePlan { get; set; } = null!;
}
