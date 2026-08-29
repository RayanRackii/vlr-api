using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class CatalogOrderStatusHistory : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required Guid OrderId { get; set; }

    public required CatalogOrderStatus Status { get; set; }

    public required CatalogActorType ActorType { get; set; }

    public Guid? ActorId { get; set; }

    public string? Reason { get; set; }

    public CatalogOrder Order { get; set; } = null!;
}
