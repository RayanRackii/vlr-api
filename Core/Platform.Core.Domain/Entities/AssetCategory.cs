using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class AssetCategory : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string Name { get; set; }

    public string? Manufacturer { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset? ScheduledDeletionAt { get; set; }

    private readonly List<Asset> _assets = [];

    public IReadOnlyCollection<Asset> Assets => _assets.AsReadOnly();
}
