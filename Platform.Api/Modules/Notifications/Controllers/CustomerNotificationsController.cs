using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Notifications.Dtos;
using Platform.Api.Modules.Notifications.Services;

namespace Platform.Api.Modules.Notifications.Controllers;

[ApiController]
[Authorize(Policy = "Customer")]
[Route("api/me/customer-notifications")]
public sealed class CustomerNotificationsController(ICustomerNotificationsService service) : ControllerBase
{
    [HttpGet("summary")]
    public Task<ActionResult<MeNotificationSummaryResponse>> Summary(CancellationToken cancellationToken) =>
        ExecuteAsync(() => service.GetSummaryAsync(User, cancellationToken));

    [HttpGet]
    public Task<ActionResult<MeNotificationsListResponse>> List(
        CancellationToken cancellationToken,
        [FromQuery] string? cursor = null,
        [FromQuery] bool unreadOnly = false,
        [FromQuery] int limit = 20) =>
        ExecuteAsync(() => service.ListAsync(User, cursor, unreadOnly, limit,
            Request.Headers.AcceptLanguage.ToString(), cancellationToken));

    [HttpPost("{id:guid}/read")]
    public Task<ActionResult<MarkNotificationsReadResponse?>> MarkRead(Guid id, CancellationToken cancellationToken) =>
        ExecuteAsync<MarkNotificationsReadResponse?>(async () =>
        {
            var updated = await service.MarkReadAsync(User, id, cancellationToken);
            return updated is null ? null : new MarkNotificationsReadResponse(updated.Value);
        });

    [HttpPost("read-all")]
    public Task<ActionResult<MarkNotificationsReadResponse>> MarkAllRead(CancellationToken cancellationToken) =>
        ExecuteAsync(async () => new MarkNotificationsReadResponse(
            await service.MarkAllReadAsync(User, cancellationToken)));

    private async Task<ActionResult<T>> ExecuteAsync<T>(Func<Task<T>> action)
    {
        try
        {
            var response = await action();
            return response is null ? NotFound() : Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
