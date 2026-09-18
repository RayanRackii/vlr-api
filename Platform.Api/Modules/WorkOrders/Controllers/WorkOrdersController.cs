using Microsoft.AspNetCore.Mvc;
using Platform.Api.Authorization;
using Platform.Api.Modules.Assets.Dtos;
using Platform.Api.Modules.Assets.Services;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;
using Platform.Core.Domain.Constants;

namespace Platform.Api.Modules.WorkOrders.Controllers;

[ApiController]
[RequireActiveModule(PlatformModules.WorkOrders)]
[Route("api/work-orders")]
public sealed class WorkOrdersController(
    IWorkOrderService workOrderService,
    IWorkOrderGenerationService workOrderGenerationService,
    IAssetRegistry assetRegistry) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Os.WorkOrdersRead)]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponse>>> List(
        [FromQuery] Guid? assetId,
        [FromQuery] Guid? maintenancePlanId,
        CancellationToken cancellationToken)
    {
        var workOrders = await workOrderService.ListAsync(
            assetId,
            maintenancePlanId,
            cancellationToken);
        return Ok(workOrders);
    }

    [HttpGet("assets")]
    [RequirePermission(Permissions.Os.WorkOrdersRead)]
    public async Task<ActionResult<IReadOnlyList<RegistryAssetListItem>>> ListAssets(
        CancellationToken cancellationToken)
    {
        var assets = await assetRegistry.ListAssetsAsync(cancellationToken);
        return Ok(assets);
    }

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Os.WorkOrdersRead)]
    public async Task<ActionResult<WorkOrderResponse>> GetById(
        Guid id,
        CancellationToken cancellationToken)
    {
        var workOrder = await workOrderService.GetByIdAsync(id, cancellationToken);

        if (workOrder is null)
        {
            return NotFound();
        }

        return Ok(workOrder);
    }

    [HttpPost]
    [RequirePermission(Permissions.Os.WorkOrdersCreate)]
    public async Task<ActionResult<WorkOrderResponse>> Create(
        [FromBody] CreateWorkOrderRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var workOrder = await workOrderService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = workOrder.Id }, workOrder);
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

    [HttpPost("from-plan")]
    [RequirePermission(Permissions.Os.WorkOrdersCreate)]
    public async Task<ActionResult<WorkOrderResponse>> CreateFromPlan(
        [FromBody] GenerateWorkOrderFromPlanRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var workOrder = await workOrderGenerationService.GenerateAsync(
                new GenerateWorkOrderCommand(
                    request.PlanId,
                    request.AssetId,
                    request.ScheduledDate,
                    request.AssignedUserId),
                cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = workOrder.Id }, workOrder);
        }
        catch (DuplicateWorkOrderException ex)
        {
            return Conflict(new { error = ex.Message, code = DuplicateWorkOrderException.Code });
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

    [HttpPatch("{id:guid}/tasks/{taskId:guid}")]
    [RequirePermission(Permissions.Os.WorkOrdersExecute)]
    public async Task<ActionResult<WorkOrderResponse>> UpdateTaskValue(
        Guid id,
        Guid taskId,
        [FromBody] UpdateWorkOrderTaskValueRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var workOrder = await workOrderService.UpdateTaskValueAsync(
                id,
                taskId,
                request,
                cancellationToken);

            if (workOrder is null)
            {
                return NotFound();
            }

            return Ok(workOrder);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPatch("{id:guid}/status")]
    [RequirePermission(Permissions.Os.WorkOrdersExecute)]
    public async Task<ActionResult<WorkOrderResponse>> UpdateStatus(
        Guid id,
        [FromBody] UpdateWorkOrderStatusRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var workOrder = await workOrderService.UpdateStatusAsync(
                id,
                request,
                cancellationToken);

            if (workOrder is null)
            {
                return NotFound();
            }

            return Ok(workOrder);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
