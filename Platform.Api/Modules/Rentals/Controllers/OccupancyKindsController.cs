using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Route("api/occupancy-kinds")]
public sealed class OccupancyKindsController(IOccupancyKindService occupancyKindService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Rentals.OccupancyKindsRead)]
    public async Task<ActionResult<IReadOnlyList<OccupancyKindResponseDto>>> List(
        CancellationToken cancellationToken)
    {
        return Ok(await occupancyKindService.ListAsync(cancellationToken));
    }

    [HttpPost]
    [RequirePermission(Permissions.Rentals.OccupancyKindsWrite)]
    public async Task<ActionResult<OccupancyKindResponseDto>> Create(
        [FromBody] UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = await occupancyKindService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(List), created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Rentals.OccupancyKindsWrite)]
    public async Task<ActionResult<OccupancyKindResponseDto>> Update(
        Guid id,
        [FromBody] UpsertOccupancyKindRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await occupancyKindService.UpdateAsync(id, request, cancellationToken));
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
}
