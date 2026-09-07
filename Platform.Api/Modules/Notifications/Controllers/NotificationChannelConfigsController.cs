using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Notifications.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationChannelConfigsController(
    INotificationChannelConfigService channelConfigService) : ControllerBase
{
    [HttpGet("channel-configs")]
    [RequirePermission(Permissions.Core.NotificationsRead)]
    public async Task<ActionResult<IReadOnlyList<NotificationChannelConfigGroupResponse>>> List(
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await channelConfigService.ListAsync(cancellationToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
    }

    [HttpPut("channel-configs")]
    [RequirePermission(Permissions.Core.NotificationsWrite)]
    public async Task<ActionResult<UpsertNotificationChannelConfigResponse>> Upsert(
        [FromBody] UpsertNotificationChannelConfigRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await channelConfigService.UpsertAsync(request, cancellationToken));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (TenantModuleInactiveException)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new { error = RequireActiveModuleAttribute.InactiveModuleError });
        }
    }
}
