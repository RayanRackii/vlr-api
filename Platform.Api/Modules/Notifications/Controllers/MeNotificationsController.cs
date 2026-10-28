using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Common;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;
using Platform.Core.Infrastructure.Persistence;

namespace Platform.Api.Modules.Notifications.Controllers;

/// <summary>
/// The signed-in B2B staff member's in-app notification inbox ("the bell"). Everything is scoped to
/// the JWT subject and JWT tenant; no user or tenant id is accepted from the client. Each action
/// asserts an ambient tenant up front so a platform-mode request (no tenant, global filter off) can
/// never read another tenant's deliveries.
/// </summary>
[ApiController]
[Authorize]
[Route("api/me/notifications")]
public sealed class MeNotificationsController(
    IMeNotificationsService service,
    ITenantProvider tenantProvider) : ControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<MeNotificationSummaryResponse>> GetSummary(
        CancellationToken cancellationToken)
    {
        var missingTenant = DirectoryAuthorization.RequireTenant(this, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        return Ok(await service.GetSummaryAsync(User, cancellationToken));
    }

    [HttpGet]
    public async Task<ActionResult<MeNotificationsListResponse>> List(
        [FromQuery] string? cursor,
        [FromQuery] bool unreadOnly,
        [FromQuery] int limit,
        CancellationToken cancellationToken)
    {
        var missingTenant = DirectoryAuthorization.RequireTenant(this, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        var effectiveLimit = limit <= 0 ? MeNotificationsService.DefaultLimit : limit;
        if (effectiveLimit > MeNotificationsService.MaxLimit)
        {
            return BadRequest(new { error = $"limit must be between 1 and {MeNotificationsService.MaxLimit}." });
        }

        var language = Request.Headers.AcceptLanguage.ToString();
        return Ok(await service.ListAsync(
            User,
            cursor,
            unreadOnly,
            effectiveLimit,
            language,
            cancellationToken));
    }

    [HttpPost("read")]
    public async Task<ActionResult<MarkNotificationsReadResponse>> MarkRead(
        [FromBody] MarkNotificationsReadRequest request,
        CancellationToken cancellationToken)
    {
        var missingTenant = DirectoryAuthorization.RequireTenant(this, tenantProvider);
        if (missingTenant is not null)
        {
            return missingTenant;
        }

        var selectors = 0;
        if (request.Ids is { Count: > 0 })
        {
            selectors++;
        }

        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            selectors++;
        }

        if (request.All == true)
        {
            selectors++;
        }

        if (selectors != 1)
        {
            return BadRequest(new
            {
                error = "Provide exactly one of ids, eventType, or all.",
            });
        }

        var updated = await service.MarkReadAsync(User, request, cancellationToken);
        return Ok(new MarkNotificationsReadResponse(updated));
    }
}
