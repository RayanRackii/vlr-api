using Platform.Core.Domain.Common;
using Platform.Core.Domain.Enums;

namespace Platform.Core.Domain.Entities;

public class NotificationTemplate : Entity
{
    public required string EventType { get; set; }

    public required NotificationChannel Channel { get; set; }

    public string Language { get; set; } = "pt-BR";

    public string? SubjectTemplate { get; set; }

    public required string BodyTemplate { get; set; }

    public string? WhatsAppTemplateName { get; set; }

    public bool IsActive { get; set; } = true;
}
