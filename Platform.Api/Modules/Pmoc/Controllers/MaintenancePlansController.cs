using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.Pmoc.Dtos;
using Platform.Api.Modules.Pmoc.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.Pmoc.Controllers;

[ApiController]
[Route("api/maintenance-plans")]
public sealed class MaintenancePlansController(
    IMaintenancePlanService maintenancePlanService,
    IAssetRegistry assetRegistry) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Pmoc.PlansRead)]
    public async Task<ActionResult<IReadOnlyList<MaintenancePlanResponse>>> List(
        CancellationToken cancellationToken)
    {
        var plans = await maintenancePlanService.ListAsync(cancellationToken);
        return Ok(plans);
    }

    [HttpGet("asset-categories")]
    [RequirePermission(Permissions.Pmoc.PlansRead)]
    public async Task<ActionResult<IReadOnlyList<RegistryCategoryListItem>>> ListAssetCategories(
        CancellationToken cancellationToken)
    {
        var categories = await assetRegistry.ListCategoriesAsync(cancellationToken);
        return Ok(categories);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Pmoc.PlansRead)]
    public async Task<ActionResult<MaintenancePlanResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var plan = await maintenancePlanService.GetByIdAsync(id, cancellationToken);

        if (plan is null)
        {
            return NotFound();
        }

        return Ok(plan);
    }

    [HttpPost]
    [RequirePermission(Permissions.Pmoc.PlansWrite)]
    public async Task<ActionResult<MaintenancePlanResponse>> Create(
        [FromBody] CreateMaintenancePlanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await maintenancePlanService.CreatePlanWithTasksAsync(
                request,
                cancellationToken);

            return CreatedAtAction(nameof(GetById), new { id = plan.Id }, plan);
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

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Pmoc.PlansWrite)]
    public async Task<ActionResult<MaintenancePlanResponse>> Update(
        Guid id,
        [FromBody] UpdateMaintenancePlanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var plan = await maintenancePlanService.UpdateAsync(id, request, cancellationToken);

            if (plan is null)
            {
                return NotFound();
            }

            return Ok(plan);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Pmoc.PlansWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await maintenancePlanService.DeleteAsync(id, cancellationToken);

        if (!deleted)
        {
            return NotFound();
        }

        return NoContent();
    }
}
