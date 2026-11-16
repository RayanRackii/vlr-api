using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Per-tenant monotonic work order number. Primary key is <see cref="TenantId"/>.
/// </summary>
public class WorkOrderNumberSequence : ITenantScoped, IMaintenanceModuleEntity
{
    public Guid TenantId { get; set; }

    public int LastNumber { get; set; }
}
