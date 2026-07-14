using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class Asset : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid UnitId { get; set; }

    public required Guid CategoryId { get; set; }

    public required string Name { get; set; }

    public required string Tag { get; set; }

    public string? Location { get; set; }

    public string? SerialNumber { get; set; }

    public DateOnly? InstallationDate { get; set; }

    public required AssetStatus Status { get; set; }

    public DateTimeOffset? ScheduledDeletionAt { get; set; }

    public Unit Unit { get; set; } = null!;

    public AssetCategory Category { get; set; } = null!;
}
