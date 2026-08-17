using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Route("api/rental-layouts")]
public sealed class RentalLayoutsController(
    IRentalLayoutService rentalLayoutService,
    IPublicTenantBinder publicTenantBinder) : ControllerBase
{
    [Authorize]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalLayoutResponseDto>>> List(
        CancellationToken cancellationToken)
    {
        return Ok(await rentalLayoutService.ListAsync(cancellationToken));
    }

    [Authorize]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RentalLayoutResponseDto>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var layout = await rentalLayoutService.GetAsync(id, cancellationToken);
        return layout is null ? NotFound() : Ok(layout);
    }

    [AllowAnonymous]
    [HttpGet("~/api/public/tenants/{subdomain}/rental-layouts")]
    public async Task<ActionResult<IReadOnlyList<RentalLayoutResponseDto>>> ListPublic(
        string subdomain,
        CancellationToken cancellationToken)
    {
        try
        {
            await publicTenantBinder.BindFromSubdomainAsync(subdomain, cancellationToken);
            var layouts = (await rentalLayoutService.ListAsync(cancellationToken))
                .Where(l => l.IsActive)
                .ToList();
            return Ok(layouts);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<RentalLayoutResponseDto>> Create(
        [FromBody] UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await rentalLayoutService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [Authorize]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RentalLayoutResponseDto>> Update(
        Guid id,
        [FromBody] UpsertRentalLayoutRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await rentalLayoutService.UpdateAsync(id, request, cancellationToken));
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
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await rentalLayoutService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}
