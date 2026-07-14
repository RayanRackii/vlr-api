using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[Authorize]
[Route("api/rental-assets")]
public sealed class RentalAssetsController(
    IRentalAssetService rentalAssetService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalAssetResponse>>> List(
        CancellationToken cancellationToken)
    {
        var assets = await rentalAssetService.ListRentableAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpGet("by-asset/{assetId:guid}")]
    public async Task<ActionResult<RentalAssetResponse>> GetByAssetId(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = await rentalAssetService.GetByAssetIdAsync(assetId, cancellationToken);

        if (asset is null)
        {
            return NotFound();
        }

        return Ok(asset);
    }
}
