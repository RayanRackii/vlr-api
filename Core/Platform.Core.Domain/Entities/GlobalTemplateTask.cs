using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Task definition belonging to a global maintenance template (no tenant isolation).
/// </summary>
public class GlobalTemplateTask : Entity
{
    public required Guid GlobalMaintenanceTemplateId { get; set; }

    public required string Title { get; set; }

    public required TaskInputType InputType { get; set; }

    /// <summary>
    /// Serialized JSON for dynamic field configuration
    /// (e.g. unit for Number, options for SingleChoice).
    /// </summary>
    public string? Configuration { get; set; }

    public required bool IsMandatory { get; set; }

    public required int Order { get; set; }

    public GlobalMaintenanceTemplate GlobalMaintenanceTemplate { get; set; } = null!;
}
