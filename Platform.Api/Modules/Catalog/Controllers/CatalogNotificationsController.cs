using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Catalog.Dtos;
using Platform.Api.Modules.Catalog.Services;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Catalog.Controllers;

[ApiController]
[Route("api/catalog")]
public sealed class CatalogNotificationsController(
    ICatalogNotificationService notificationService) : ControllerBase
{
    [HttpGet("notifications")]
    [RequirePermission(Permissions.Catalog.NotificationsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogNotificationDeliveryResponse>>> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? eventType,
        [FromQuery] NotificationRecipientKind? recipientKind,
        [FromQuery] NotificationChannel? channel,
        [FromQuery] NotificationDeliveryStatus? status,
        CancellationToken cancellationToken)
    {
        var items = await notificationService.ListAsync(
            new CatalogNotificationListQuery(from, to, eventType, recipientKind, channel, status),
            cancellationToken);
        return Ok(items);
    }

    [HttpPost("notifications/deliveries/{id:guid}/resend")]
    [RequirePermission(Permissions.Catalog.NotificationsResend)]
    public async Task<ActionResult<CatalogNotificationDeliveryResponse>> Resend(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await notificationService.ResendAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpGet("notification-channels")]
    [RequirePermission(Permissions.Catalog.NotificationsRead)]
    public async Task<ActionResult<IReadOnlyList<CatalogChannelConfigItem>>> ListChannels(
        CancellationToken cancellationToken)
    {
        return Ok(await notificationService.ListChannelConfigsAsync(cancellationToken));
    }

    [HttpPut("notification-channels")]
    [RequirePermission(Permissions.Catalog.NotificationsResend)]
    public async Task<ActionResult<CatalogChannelConfigItem>> UpsertChannel(
        [FromBody] UpsertCatalogChannelConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await notificationService.UpsertChannelConfigAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
