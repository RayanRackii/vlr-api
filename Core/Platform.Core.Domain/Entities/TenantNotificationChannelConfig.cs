using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class TenantNotificationChannelConfig : Entity, ITenantScoped
{
    public required Guid TenantId { get; set; }

    public required string EventType { get; set; }

    public required NotificationChannel Channel { get; set; }

    public bool IsActive { get; set; }
}
