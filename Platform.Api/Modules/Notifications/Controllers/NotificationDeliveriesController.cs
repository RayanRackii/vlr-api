using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Notifications;
using Platform.Core.Domain.Constants;
using Platform.Core.Domain.Enums;

namespace Platform.Api.Modules.Notifications.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationDeliveriesController(INotificationDeliveryService deliveries) : ControllerBase
{
    [HttpGet("deliveries")]
    [RequirePermission(Permissions.Core.NotificationsRead)]
    public async Task<ActionResult<NotificationDeliveryPageResponse>> List(
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery(Name = "event")] string? eventType,
        [FromQuery] NotificationChannel? channel,
        [FromQuery] NotificationDeliveryStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = NotificationDeliveryPages.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!NotificationDeliveryPages.TryCreate(page, pageSize, out var error))
        {
            return BadRequest(new { error });
        }

        if (from is { } periodStart && to is { } periodEnd && periodStart > periodEnd)
        {
            return BadRequest(new { error = "Period start must be before period end." });
        }

        try
        {
            return Ok(await deliveries.ListPageAsync(
                new NotificationDeliveryFilter(from, to, eventType, null, channel, status),
                page,
                pageSize,
                cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPost("deliveries/{id:guid}/resend")]
    [RequirePermission(Permissions.Core.NotificationsResend)]
    public async Task<ActionResult<NotificationDeliveryResponse>> Resend(
        Guid id,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await deliveries.ResendAsync(id, cancellationToken));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }
}
