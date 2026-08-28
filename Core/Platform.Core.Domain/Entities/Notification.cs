using Platform.Core.Domain.Common;

namespace Platform.Core.Domain.Entities;

public class Notification : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string EventType { get; set; }

    public required string AggregateType { get; set; }

    public required Guid AggregateId { get; set; }

    public Dictionary<string, string?> Payload { get; set; } = new(StringComparer.Ordinal);

    private readonly List<NotificationDelivery> _deliveries = [];

    public IReadOnlyCollection<NotificationDelivery> Deliveries => _deliveries.AsReadOnly();

    public void AddDelivery(NotificationDelivery delivery) => _deliveries.Add(delivery);
}
