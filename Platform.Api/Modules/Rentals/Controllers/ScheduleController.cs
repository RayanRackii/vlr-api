using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Route("api/schedule")]
public sealed class ScheduleController(
    IScheduleService scheduleService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    [Authorize]
    [HttpGet("templates")]
    public async Task<ActionResult<IReadOnlyList<ScheduleTemplateResponseDto>>> ListTemplates(
        [FromQuery] Guid? rentalAssetId,
        [FromQuery] Guid[]? rentalAssetIds,
        [FromQuery] DayOfWeek? dayOfWeek,
        CancellationToken cancellationToken)
    {
        return Ok(await scheduleService.ListTemplatesAsync(
            rentalAssetId,
            cancellationToken,
            rentalAssetIds,
            dayOfWeek));
    }

    [Authorize]
    [HttpPost("templates")]
    public async Task<ActionResult<ScheduleTemplateResponseDto>> CreateTemplate(
        [FromBody] UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await scheduleService.CreateTemplateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(ListTemplates), created);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPut("templates/{id:guid}")]
    public async Task<ActionResult<ScheduleTemplateResponseDto>> UpdateTemplate(
        Guid id,
        [FromBody] UpsertScheduleTemplateRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await scheduleService.UpdateTemplateAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpDelete("templates/{id:guid}")]
    public async Task<IActionResult> DeleteTemplate(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await scheduleService.DeleteTemplateAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("templates/seed-default")]
    public async Task<ActionResult<SeedDefaultTemplatesResponseDto>> SeedDefaultTemplates(
        [FromBody] SeedDefaultTemplatesRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await scheduleService.SeedDefaultTemplatesAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("templates/apply-weekly-rule")]
    public async Task<ActionResult<ApplyWeeklyRuleResponseDto>> ApplyWeeklyRule(
        [FromBody] ApplyWeeklyRuleRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await scheduleService.ApplyWeeklyRuleAsync(request, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpGet("days/{date}")]
    public async Task<ActionResult<DayScheduleResponseDto>> GetDayAdmin(
        DateOnly date,
        [FromQuery] Guid? rentalAssetId,
        [FromQuery] Guid[]? rentalAssetIds,
        CancellationToken cancellationToken)
    {
        return Ok(await scheduleService.GetDayAsync(
            date,
            rentalAssetId,
            customerFacing: false,
            cancellationToken,
            rentalAssetIds));
    }

    [AllowAnonymous]
    [HttpGet("~/api/public/tenants/{subdomain}/schedule/days/{date}")]
    public async Task<ActionResult<DayScheduleResponseDto>> GetDayPublic(
        string subdomain,
        DateOnly date,
        [FromQuery] Guid? rentalAssetId,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(subdomain, cancellationToken);
            return Ok(await scheduleService.GetDayAsync(date, rentalAssetId, customerFacing: true, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("days/publish")]
    public async Task<ActionResult<object>> PublishDay(
        [FromBody] PublishDayRequestDto request,
        CancellationToken cancellationToken)
    {
        var created = await scheduleService.PublishDayAsync(request, cancellationToken);
        return Ok(new { created });
    }

    [Authorize]
    [HttpPost("slots")]
    public async Task<ActionResult<SlotResponseDto>> UpsertSlot(
        [FromBody] UpsertSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await scheduleService.UpsertSlotAsync(request, cancellationToken));
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("slots/{id:guid}/cancel")]
    public async Task<IActionResult> CancelSlot(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await scheduleService.CancelSlotAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("slots/daily-occurrence")]
    public async Task<ActionResult<SlotResponseDto>> ApplyDailyOccurrence(
        [FromBody] ApplyDailyOccurrenceRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var slot = await scheduleService.ApplyDailyOccurrenceAsync(request, cancellationToken);
            return Ok(slot);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize(Policy = "Customer")]
    [HttpPost("slots/book")]
    public async Task<ActionResult<ReservationResponseDto>> BookSlot(
        [FromBody] BookSlotRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var customerId = ResolveCustomerId();
            var reservation = await scheduleService.BookSlotAsync(customerId, request, cancellationToken);
            return Ok(reservation);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    private Guid ResolveCustomerId()
    {
        var customerIdClaim = User.FindFirst(CustomerClaimTypes.CustomerId)?.Value;

        if (!Guid.TryParse(customerIdClaim, out var customerId))
        {
            throw new UnauthorizedAccessException(
                "The access token is missing a valid customer_id claim.");
        }

        return customerId;
    }
}
