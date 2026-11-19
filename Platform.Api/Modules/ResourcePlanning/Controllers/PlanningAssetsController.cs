using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.ResourcePlanning.Dtos;
using Platform.Api.Modules.ResourcePlanning.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.ResourcePlanning.Controllers;

/// <summary>
/// Plannable assets, planning profile and weekly working hours. Staff only; nothing public or
/// portal. Gated by the <c>resource-planning</c> module alone (not by inventory or rentals).
/// </summary>
[ApiController]
[RequireActiveModule(PlatformModules.ResourcePlanning)]
[Route("api/planning/assets")]
public sealed class PlanningAssetsController(IPlanningProfileService profileService) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public async Task<ActionResult<IReadOnlyList<PlanningAssetResponse>>> List(
        [FromQuery] string? search,
        [FromQuery] Guid? unitId,
        [FromQuery] string? plannable,
        CancellationToken cancellationToken)
    {
        try
        {
            var assets = await profileService.ListAssetsAsync(search, unitId, plannable, cancellationToken);
            return Ok(assets);
        }
        catch (PlanningException ex)
        {
            return ToResult(ex);
        }
    }

    [HttpGet("{assetId:guid}/profile")]
    [RequirePermission(Permissions.ResourcePlanning.AgendaRead)]
    public async Task<ActionResult<AssetPlanningProfileResponse>> GetProfile(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var profile = await profileService.GetProfileAsync(assetId, cancellationToken);
        if (profile is null)
        {
            return ToResult(PlanningException.AssetNotFound(assetId));
        }

        return Ok(profile);
    }

    [HttpPut("{assetId:guid}/profile")]
    [RequirePermission(Permissions.ResourcePlanning.AvailabilityWrite)]
    public async Task<ActionResult<AssetPlanningProfileResponse>> PutProfile(
        Guid assetId,
        [FromBody] PutAssetPlanningProfileRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var profile = await profileService.UpsertProfileAsync(assetId, request, cancellationToken);
            return Ok(profile);
        }
        catch (PlanningException ex)
        {
            return ToResult(ex);
        }
    }

    private ObjectResult ToResult(PlanningException ex) =>
        StatusCode(ex.StatusCode, new { error = ex.Message, code = ex.Code });
}
