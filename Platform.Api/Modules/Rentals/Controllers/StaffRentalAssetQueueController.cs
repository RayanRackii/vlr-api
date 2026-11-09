using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

/// <summary>
/// Staff read of one bookable resource's waiting queue.
/// The tenant comes from the staff JWT. A resource outside that tenant is 404.
/// </summary>
[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rentals/rental-assets")]
public sealed class StaffRentalAssetQueueController(IReservationQueueService reservationQueueService) : ControllerBase
{
    [RequirePermission(Permissions.Rentals.ReservationsRead)]
    [HttpGet("{rentalAssetId:guid}/queue")]
    public async Task<ActionResult<StaffReservationQueueDto>> GetQueue(
        Guid rentalAssetId,
        [FromQuery] DateOnly? openingDate,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await reservationQueueService.GetStaffRosterAsync(
                rentalAssetId,
                openingDate,
                cancellationToken));
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
