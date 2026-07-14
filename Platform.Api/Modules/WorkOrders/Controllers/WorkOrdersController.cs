using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Platform.Api.Modules.WorkOrders.Dtos;
using Platform.Api.Modules.WorkOrders.Services;

namespace Platform.Api.Modules.WorkOrders.Controllers;

[ApiController]
[Authorize]
[Route("api/work-orders")]
public sealed class WorkOrdersController(
    IWorkOrderService workOrderService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<WorkOrderResponse>>> List(
        [FromQuery] Guid? assetId,
        CancellationToken cancellationToken)
    {
        var workOrders = await workOrderService.ListAsync(assetId, cancellationToken);
        return Ok(workOrders);
    }

    [HttpGet("{id:guid}")]
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

    [HttpPatch("{id:guid}/tasks/{taskId:guid}")]
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
