using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authentication;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Rentals.Dtos;
using Platform.Api.Modules.Rentals.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Rentals.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.Rentals)]
[Route("api/rental-assets")]
public sealed class RentalAssetsController(
    IRentalAssetService rentalAssetService,
    IReservationQueueService reservationQueueService,
    IAssetRegistry assetRegistry) : ControllerBase
{
    [RequirePermission(Permissions.Rentals.AssetsRead)]
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RentalAssetResponse>>> List(
        CancellationToken cancellationToken)
    {
        var assets = await rentalAssetService.ListRentableAsync(cancellationToken);
        return Ok(assets);
    }

    [RequirePermission(Permissions.Rentals.AssetsRead)]
    [HttpGet("categories")]
    public async Task<ActionResult<IReadOnlyList<RegistryCategoryListItem>>> ListCategories(
        CancellationToken cancellationToken)
    {
        var categories = await assetRegistry.ListCategoriesAsync(cancellationToken);
        return Ok(categories);
    }

    [RequirePermission(Permissions.Rentals.AssetsRead)]
    [HttpGet("families")]
    public async Task<ActionResult<IReadOnlyList<AssetFamilyDetailResponse>>> ListFamilies(
        CancellationToken cancellationToken)
    {
        var families = await assetRegistry.ListActiveFamiliesAsync(cancellationToken);
        return Ok(families);
    }

    [RequirePermission(Permissions.Rentals.AssetsWrite)]
    [HttpPost]
    public async Task<ActionResult<RentalAssetResponse>> Create(
        [FromBody] CreateRentableRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetRegistry.CreateRentableAsync(request, cancellationToken);
            var rental = await rentalAssetService.GetByAssetIdAsync(asset.Id, cancellationToken)
                ?? throw new InvalidOperationException("Rentable configuration was not created.");

            return CreatedAtAction(nameof(GetByAssetId), new { assetId = asset.Id }, rental);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [RequirePermission(Permissions.Rentals.AssetsRead)]
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

    [RequirePermission(Permissions.Rentals.AssetsWrite)]
    [HttpPut("schedule-policy")]
    public async Task<ActionResult<BulkUpdateRentalSchedulePolicyResponseDto>> UpdateSchedulePolicyBulk(
        [FromBody] BulkUpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(
                await rentalAssetService.UpdateSchedulePolicyBulkAsync(request, cancellationToken));
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

    [RequirePermission(Permissions.Rentals.AssetsWrite)]
    [HttpPut("{id:guid}/schedule-policy")]
    public async Task<ActionResult<RentalAssetResponse>> UpdateSchedulePolicy(
        Guid id,
        [FromBody] UpdateRentalSchedulePolicyRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(
                await rentalAssetService.UpdateSchedulePolicyAsync(id, request, cancellationToken));
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

    [RequirePermission(Permissions.Rentals.AssetsWrite)]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<RentalAssetResponse>> Update(
        Guid id,
        [FromBody] UpdateRentableRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetRegistry.UpdateRentableAsync(id, request, cancellationToken);
            var rental = await rentalAssetService.GetByAssetIdAsync(asset.Id, cancellationToken)
                ?? throw new InvalidOperationException("Rentable configuration was not found.");

            return Ok(rental);
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

    /// <summary>
    /// Current daily waiting-queue status for the authenticated B2C customer.
    /// Class-level [Authorize] is not used: the default policy rejects Customer JWTs.
    /// </summary>
    [Authorize(Policy = "Customer")]
    [HttpGet("{id:guid}/queue")]
    public async Task<ActionResult<ReservationQueueStatusDto>> GetQueue(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await ExecuteQueueAsync(
            () => reservationQueueService.GetStatusAsync(id, ResolveCustomerId(), cancellationToken));
    }

    [Authorize(Policy = "Customer")]
    [HttpPost("{id:guid}/queue/join")]
    public async Task<ActionResult<ReservationQueueStatusDto>> JoinQueue(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await ExecuteQueueAsync(
            () => reservationQueueService.JoinAsync(id, ResolveCustomerId(), cancellationToken));
    }

    [Authorize(Policy = "Customer")]
    [HttpPost("{id:guid}/queue/leave")]
    public async Task<ActionResult<ReservationQueueStatusDto>> LeaveQueue(
        Guid id,
        CancellationToken cancellationToken)
    {
        return await ExecuteQueueAsync(
            () => reservationQueueService.LeaveAsync(id, ResolveCustomerId(), cancellationToken));
    }

    private async Task<ActionResult<ReservationQueueStatusDto>> ExecuteQueueAsync(
        Func<Task<ReservationQueueStatusDto>> action)
    {
        try
        {
            return Ok(await action());
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
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
