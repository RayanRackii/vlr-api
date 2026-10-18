using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

/// <summary>
/// Image stored for one work-order task. The task answer is this row's id.
/// </summary>
public class WorkOrderTaskFile : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid WorkOrderId { get; set; }

    public required Guid WorkOrderTaskId { get; set; }

    public required string StorageKey { get; set; }

    public required string FileName { get; set; }

    public required string ContentType { get; set; }

    public required long SizeBytes { get; set; }
}
